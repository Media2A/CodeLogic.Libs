using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>
/// Sends raw protocol commands: FTP commands such as <c>SITE</c> on FTP connections, and SSH shell
/// commands on SFTP connections. Advertised with <see cref="StorageFeature.RawCommands"/> only when the
/// connection sets <c>AllowRawCommands</c>, because commands are not confined to the mounted root.
/// </summary>
public interface IStorageCommandService
{
    /// <summary>Runs one command and returns its reply.</summary>
    /// <param name="command">Command text, such as <c>SITE CHMOD 640 file</c> or <c>df -h</c>.</param>
    /// <param name="cancellationToken">Token used to cancel the command.</param>
    /// <returns>The reply; a rejected command or non-zero exit is a successful result with <see cref="StorageCommandResult.Succeeded"/> false.</returns>
    Task<Result<StorageCommandResult>> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default);
}

/// <summary>The reply to a raw command.</summary>
/// <param name="Succeeded">Whether the server accepted the command (FTP 1xx–3xx, or SSH exit status 0).</param>
/// <param name="Code">FTP reply code, or SSH exit status.</param>
/// <param name="Output">FTP reply text, or SSH standard output.</param>
/// <param name="ErrorOutput">SSH standard error; empty for FTP.</param>
public sealed record StorageCommandResult(bool Succeeded, string Code, string Output, string ErrorOutput);

/// <summary>Reports free and used space, advertised with <see cref="StorageFeature.SpaceInfo"/>.</summary>
public interface IStorageSpaceService
{
    /// <summary>Returns space figures for the volume or quota that holds a path.</summary>
    /// <param name="path">Path relative to the mounted root; the root by default.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    /// <returns>Known figures, or <c>storage.unsupported</c> when the server does not report them.</returns>
    Task<Result<StorageSpaceInfo>> GetSpaceAsync(string path = "", CancellationToken cancellationToken = default);
}

/// <summary>Space figures; any may be unknown.</summary>
/// <param name="TotalBytes">Capacity of the volume or quota.</param>
/// <param name="AvailableBytes">Bytes the connection can still write.</param>
/// <param name="UsedBytes">Bytes in use.</param>
public sealed record StorageSpaceInfo(long? TotalBytes, long? AvailableBytes, long? UsedBytes);
