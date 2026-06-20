using CL.TwoFactorAuth.Models;
using CL.TwoFactorAuth.Services;
using Xunit;

namespace TwoFactorAuth.Tests;

// Offline unit tests for CL.TwoFactorAuth. The TOTP + QR services are pure crypto with
// no network or runtime dependency, so they are instantiated directly with a config model.

public class TwoFactorAuthenticatorTests
{
    private static TwoFactorAuthenticator NewAuth() => new(new TwoFactorAuthConfig());

    [Fact]
    public void GenerateSecretKey_returns_nonempty_base32_and_two_calls_differ()
    {
        var auth = NewAuth();
        var a = auth.GenerateSecretKey();
        var b = auth.GenerateSecretKey();

        Assert.False(string.IsNullOrWhiteSpace(a));
        // Base32 alphabet: A-Z and 2-7 (no padding from KeyGeneration output).
        Assert.All(a, c => Assert.True((c >= 'A' && c <= 'Z') || (c >= '2' && c <= '7'), $"unexpected char '{c}'"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GenerateNewKey_produces_key_with_secret_and_provisioning_uri()
    {
        var auth = NewAuth();
        var key = auth.GenerateNewKey("MyApp", "alice@example.com");

        Assert.False(string.IsNullOrWhiteSpace(key.SecretKey));
        Assert.StartsWith("otpauth://", key.ProvisioningUri);
        Assert.Contains("secret=" + key.SecretKey, key.ProvisioningUri);
    }

    [Fact]
    public void Totp_round_trip_validates()
    {
        var auth = NewAuth();
        var secret = auth.GenerateSecretKey();

        var code = auth.GenerateTotpCode(secret);
        Assert.True(code.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(code.Value));

        var result = auth.ValidateTotp(code.Value!, secret);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Wrong_code_is_rejected()
    {
        var auth = NewAuth();
        var secret = auth.GenerateSecretKey();

        var code = auth.GenerateTotpCode(secret);
        Assert.True(code.IsSuccess);

        // Pick a code guaranteed to differ from the real one.
        var wrong = code.Value == "000000" ? "111111" : "000000";
        var result = auth.ValidateTotp(wrong, secret);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Code_from_one_secret_does_not_validate_against_another()
    {
        var auth = NewAuth();
        var secretA = auth.GenerateSecretKey();
        var secretB = auth.GenerateSecretKey();

        var codeA = auth.GenerateTotpCode(secretA);
        Assert.True(codeA.IsSuccess);

        var result = auth.ValidateTotp(codeA.Value!, secretB);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void GetSecondsUntilExpiry_is_within_time_step()
    {
        var config = new TwoFactorAuthConfig();
        var auth = new TwoFactorAuthenticator(config);

        var seconds = auth.GetSecondsUntilExpiry();
        Assert.True(seconds > 0, $"got {seconds}");
        Assert.True(seconds <= config.TimeStepSeconds, $"got {seconds}");
    }

    [Fact]
    public void GetCurrentTimeWindow_is_positive()
    {
        var auth = NewAuth();
        Assert.True(auth.GetCurrentTimeWindow() > 0);
    }
}

public class QrCodeGeneratorTests
{
    private static readonly byte[] PngSignature = { 0x89, (byte)'P', (byte)'N', (byte)'G' };

    private static (TwoFactorAuthenticator auth, QrCodeGenerator qr, TwoFactorKey key) Setup()
    {
        var config = new TwoFactorAuthConfig();
        var auth = new TwoFactorAuthenticator(config);
        var qr = new QrCodeGenerator(config);
        var key = auth.GenerateNewKey("MyApp", "bob@example.com");
        return (auth, qr, key);
    }

    [Fact]
    public void GenerateQrCodePng_returns_png_bytes()
    {
        var (_, qr, key) = Setup();

        var result = qr.GenerateQrCodePng(key);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.NotEmpty(result.Value!);
        Assert.Equal(PngSignature, result.Value!.Take(4).ToArray());
    }

    [Fact]
    public void GenerateQrCodeBase64_returns_nonempty()
    {
        var (_, qr, key) = Setup();

        var result = qr.GenerateQrCodeBase64(key);
        Assert.True(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Value));
    }

    [Fact]
    public void GenerateQrCodeDataUri_has_png_data_uri_prefix()
    {
        var (_, qr, key) = Setup();

        var result = qr.GenerateQrCodeDataUri(key);
        Assert.True(result.IsSuccess);
        Assert.StartsWith("data:image/png;base64,", result.Value!);
    }
}
