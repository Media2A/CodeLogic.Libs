using System.Net.Sockets;
using System.Security.Authentication;
using CL.Storage.Errors;
using CL.Storage.Providers;
using CL.Storage.Providers.Ftp;
using CL.Storage.Providers.Sftp;
using FluentFTP.Exceptions;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using Renci.SshNet.Sftp;
using Xunit;

namespace Storage.Tests;

/// <summary>
/// Pins how provider exceptions become storage error codes, so callers can tell a bad password from a
/// missing file, a full disk, or a dropped connection instead of receiving one generic provider error.
/// </summary>
public sealed class ErrorMappingTests
{
    [Theory]
    [InlineData("421", "Too many users, try again later", StorageErrors.ServerBusyCode)]
    [InlineData("421", "Service closing control connection", StorageErrors.ConnectionLostCode)]
    [InlineData("425", "Can't open data connection", StorageErrors.ConnectionLostCode)]
    [InlineData("426", "Connection closed; transfer aborted", StorageErrors.ConnectionLostCode)]
    [InlineData("530", "Not logged in", StorageErrors.AuthenticationFailedCode)]
    [InlineData("550", "Permission denied", StorageErrors.PermissionDeniedCode)]
    [InlineData("550", "No such file or directory", StorageErrors.NotFoundCode)]
    [InlineData("550", "Directory not empty", StorageErrors.ConflictCode)]
    [InlineData("450", "File busy", StorageErrors.UnavailableCode)]
    [InlineData("452", "Insufficient storage space", StorageErrors.QuotaExceededCode)]
    [InlineData("552", "Exceeded storage allocation", StorageErrors.QuotaExceededCode)]
    [InlineData("553", "File name not allowed", StorageErrors.InvalidPathCode)]
    [InlineData("502", "Command not implemented", StorageErrors.UnsupportedCode)]
    [InlineData("534", "Request denied for policy reasons", StorageErrors.TlsFailureCode)]
    [InlineData("451", "Local error in processing", StorageErrors.UnavailableCode)]
    [InlineData("500", "Syntax error", StorageErrors.ProviderErrorCode)]
    public void Ftp_reply_codes_map_to_specific_errors(string reply, string message, string expected)
    {
        var error = FtpStorageBackend.Map(new FtpCommandException(reply, message), "Op");

        Assert.Equal(expected, error.Code);
        Assert.True(StorageErrorInfo.TryGetDetail(error, StorageErrorInfo.FtpReplyKey, out var detail));
        Assert.Equal(reply, detail);
    }

    [Fact]
    public void Ftp_login_failure_is_authentication_not_generic_provider_error()
    {
        var error = FtpStorageBackend.Map(new FtpAuthenticationException("530", "Login incorrect."), "Connect");

        Assert.Equal(StorageErrors.AuthenticationFailedCode, error.Code);
    }

    [Fact]
    public void Ftp_tls_failures_are_not_reported_as_credential_failures()
    {
        Assert.Equal(StorageErrors.TlsFailureCode, FtpStorageBackend.Map(new AuthenticationException("handshake"), "Op").Code);
        Assert.Equal(StorageErrors.TlsFailureCode, FtpStorageBackend.Map(new FtpInvalidCertificateException("pin"), "Op").Code);
    }

    [Fact]
    public void Ftp_transport_failures_distinguish_refused_from_dropped()
    {
        var refused = FtpStorageBackend.Map(new SocketException((int)SocketError.ConnectionRefused), "Op");
        var reset = FtpStorageBackend.Map(new IOException("reset", new SocketException((int)SocketError.ConnectionReset)), "Op");
        var closed = FtpStorageBackend.Map(new IOException("stream closed"), "Op");

        Assert.Equal(StorageErrors.ConnectionFailedCode, refused.Code);
        Assert.Equal(StorageErrors.ConnectionLostCode, reset.Code);
        Assert.Equal(StorageErrors.ConnectionLostCode, closed.Code);
        Assert.All(new[] { refused, reset, closed }, error => Assert.True(StorageErrorInfo.IsTransient(error)));
    }

    [Fact]
    public void Ftp_error_messages_do_not_leak_server_text()
    {
        var error = FtpStorageBackend.Map(new FtpCommandException("550", "secret /home/alice/.ssh denied"), "Op");

        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Details ?? string.Empty, StringComparison.Ordinal);
    }

    public static TheoryData<Exception, string> SftpCases => new()
    {
        { new SftpPathNotFoundException("missing"), StorageErrors.NotFoundCode },
        { new SftpPermissionDeniedException("denied"), StorageErrors.PermissionDeniedCode },
        { new SshAuthenticationException("bad key"), StorageErrors.AuthenticationFailedCode },
        { new SshOperationTimeoutException("slow"), StorageErrors.TimeoutCode },
        { new ProxyException("proxy"), StorageErrors.ConnectionFailedCode },
        { new SftpException(StatusCode.ConnectionLost, "lost"), StorageErrors.ConnectionLostCode },
        { new SftpException(StatusCode.OperationUnsupported, "nope"), StorageErrors.UnsupportedCode },
        { new SftpException(StatusCode.Failure, "Disk quota exceeded"), StorageErrors.QuotaExceededCode },
        { new SftpException(StatusCode.Failure, "Failure"), StorageErrors.ProviderErrorCode },
        { new SshConnectionException("bye", DisconnectReason.HostKeyNotVerifiable), StorageErrors.HostKeyRejectedCode },
        { new SshConnectionException("bye", DisconnectReason.TooManyConnections), StorageErrors.ServerBusyCode },
        { new SshConnectionException("bye", DisconnectReason.NoMoreAuthenticationMethodsAvailable), StorageErrors.AuthenticationFailedCode },
        { new SshConnectionException("bye", DisconnectReason.ConnectionLost), StorageErrors.ConnectionLostCode },
        { new SshConnectionException("kex", DisconnectReason.KeyExchangeFailed), StorageErrors.ConnectionFailedCode },
        { new SocketException((int)SocketError.HostNotFound), StorageErrors.ConnectionFailedCode },
        { new SftpHostKeyRejectedException("SHA256:abc", new SshConnectionException("kex")), StorageErrors.HostKeyRejectedCode }
    };

    [Theory]
    [MemberData(nameof(SftpCases))]
    public void Sftp_exceptions_map_to_specific_errors(Exception exception, string expected)
    {
        Assert.Equal(expected, SftpStorageBackend.Map(exception, "Op").Code);
    }

    [Fact]
    public void Sftp_host_key_rejection_reports_the_presented_fingerprint()
    {
        var error = SftpStorageBackend.Map(new SftpHostKeyRejectedException("SHA256:abc", new SshConnectionException("kex")), "Connect");

        Assert.True(StorageErrorInfo.TryGetDetail(error, "presentedFingerprint", out var fingerprint));
        Assert.Equal("SHA256:abc", fingerprint);
    }

    [Theory]
    [InlineData(401, StorageErrors.AuthenticationFailedCode)]
    [InlineData(403, StorageErrors.PermissionDeniedCode)]
    [InlineData(404, StorageErrors.NotFoundCode)]
    [InlineData(408, StorageErrors.TimeoutCode)]
    [InlineData(409, StorageErrors.ConflictCode)]
    [InlineData(412, StorageErrors.ConflictCode)]
    [InlineData(413, StorageErrors.TooLargeCode)]
    [InlineData(423, StorageErrors.ConflictCode)]
    [InlineData(429, StorageErrors.ServerBusyCode)]
    [InlineData(500, StorageErrors.UnavailableCode)]
    [InlineData(503, StorageErrors.ServerBusyCode)]
    [InlineData(504, StorageErrors.TimeoutCode)]
    [InlineData(507, StorageErrors.QuotaExceededCode)]
    [InlineData(418, StorageErrors.ProviderErrorCode)]
    public void Http_statuses_map_to_specific_errors(int status, string expected)
    {
        var error = ProviderErrorMapper.FromHttpStatus(status, "Op", "WebDAV");

        Assert.Equal(expected, error.Code);
        Assert.True(StorageErrorInfo.TryGetDetail(error, StorageErrorInfo.HttpStatusKey, out var detail));
        Assert.Equal(status.ToString(), detail);
    }

    [Fact]
    public void Retry_after_is_carried_through_error_details()
    {
        var error = ProviderErrorMapper.FromHttpStatus(429, "Op", "Swift", TimeSpan.FromSeconds(7));

        Assert.True(StorageErrorInfo.TryGetRetryAfter(error, out var delay));
        Assert.Equal(TimeSpan.FromSeconds(7), delay);
    }

    [Fact]
    public void Tls_failure_inside_http_request_is_classified_as_tls()
    {
        var exception = new HttpRequestException("ssl", new AuthenticationException("remote certificate invalid"));

        Assert.Equal(StorageErrors.TlsFailureCode, ProviderErrorMapper.FromTransport(exception, "Op", "WebDAV")!.Code);
    }

    [Fact]
    public void Unknown_exceptions_are_left_for_the_provider_fallback()
    {
        Assert.Null(ProviderErrorMapper.FromTransport(new InvalidOperationException(), "Op", "WebDAV"));
    }

    [Theory]
    [InlineData(StorageErrors.TimeoutCode, true)]
    [InlineData(StorageErrors.UnavailableCode, true)]
    [InlineData(StorageErrors.ConnectionFailedCode, true)]
    [InlineData(StorageErrors.ConnectionLostCode, true)]
    [InlineData(StorageErrors.ServerBusyCode, true)]
    [InlineData(StorageErrors.AuthenticationFailedCode, false)]
    [InlineData(StorageErrors.PermissionDeniedCode, false)]
    [InlineData(StorageErrors.HostKeyRejectedCode, false)]
    [InlineData(StorageErrors.TlsFailureCode, false)]
    [InlineData(StorageErrors.QuotaExceededCode, false)]
    [InlineData(StorageErrors.NotFoundCode, false)]
    public void Only_retryable_codes_are_transient(string code, bool transient)
    {
        var error = code switch
        {
            StorageErrors.TimeoutCode => StorageErrors.Timeout("x"),
            StorageErrors.UnavailableCode => StorageErrors.Unavailable("x"),
            StorageErrors.ConnectionFailedCode => StorageErrors.ConnectionFailed("x"),
            StorageErrors.ConnectionLostCode => StorageErrors.ConnectionLost("x"),
            StorageErrors.ServerBusyCode => StorageErrors.ServerBusy("x"),
            StorageErrors.AuthenticationFailedCode => StorageErrors.AuthenticationFailed("x"),
            StorageErrors.PermissionDeniedCode => StorageErrors.PermissionDenied("x"),
            StorageErrors.HostKeyRejectedCode => StorageErrors.HostKeyRejected("x"),
            StorageErrors.TlsFailureCode => StorageErrors.TlsFailure("x"),
            StorageErrors.QuotaExceededCode => StorageErrors.QuotaExceeded("x"),
            _ => StorageErrors.NotFound("x")
        };

        Assert.Equal(transient, StorageErrorInfo.IsTransient(error));
    }
}
