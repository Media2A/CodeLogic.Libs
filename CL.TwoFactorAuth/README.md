# CodeLogic.TwoFactorAuth

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.TwoFactorAuth)](https://www.nuget.org/packages/CodeLogic.TwoFactorAuth)

TOTP two-factor authentication with QR code generation for [CodeLogic](https://github.com/Media2A/CodeLogic) applications.

## Install

```
dotnet add package CodeLogic.TwoFactorAuth
```

## Quick Start

```csharp
var tfaLib = new TwoFactorAuthLibrary();
// After library initialization via the CodeLogic framework:

// Generate a new key bound to an issuer + user (creates a fresh Base32 secret)
TwoFactorKey key = tfaLib.GenerateNewKey("MyApp", "user@example.com");

// Persist key.SecretKey alongside the user record for later validation

// Render a QR code as a data URI for an authenticator app (Google Authenticator / Authy / 1Password)
Result<string> qrDataUri = tfaLib.GenerateQrCodeDataUri(key);
if (qrDataUri.IsSuccess)
{
    // Embed qrDataUri.Value in an <img src="..."> tag
}

// Validate a code entered by the user
TotpValidationResult result = tfaLib.ValidateTotp("123456", key.SecretKey);
Console.WriteLine($"Valid: {result.IsValid}");
```

## Features

- **Secret key generation** — cryptographically random Base32-encoded TOTP secrets (`GenerateSecretKey`)
- **Provisioning keys** — `GenerateNewKey(issuer, user)` returns a `TwoFactorKey` exposing a standard `otpauth://` `ProvisioningUri`
- **TOTP generation & validation** — `GenerateTotpCode` and `ValidateTotp` with configurable time-step and drift window; validation returns a `TotpValidationResult` (including the matched window offset)
- **Code lifetime helpers** — `GetSecondsUntilExpiry()` and `GetCurrentTimeWindow()`
- **QR code rendering** — PNG bytes, BMP bytes, Base64, data URI, and save-to-file output with configurable module size and error-correction level
- **Google Authenticator compatible** — standard `otpauth://totp/...` URI format (RFC 6238)
- **Event integration** — `SecretKeyGeneratedEvent` and `TotpValidatedEvent` are published to the CodeLogic event bus
- **Health check** — exercises live TOTP key generation

## Services

The library exposes two services plus convenience pass-throughs:

| Member | Returns | Purpose |
|--------|---------|---------|
| `Authenticator` | `TwoFactorAuthenticator` | TOTP generation/validation service |
| `QrCode` | `QrCodeGenerator` | QR code rendering service |
| `GenerateSecretKey()` | `string` | New Base32 secret |
| `GenerateNewKey(issuer, user)` | `TwoFactorKey` | New key + provisioning URI |
| `ValidateTotp(code, secretKey)` | `TotpValidationResult` | Verify a 6-digit code |
| `GenerateQrCodeDataUri(key)` | `Result<string>` | `data:image/png;base64,...` URI |

### TwoFactorAuthenticator

```csharp
var auth = tfaLib.Authenticator;

string secret = auth.GenerateSecretKey();
Result<string> codeResult = auth.GenerateTotpCode(secret);

// userId is optional — when supplied, a TotpValidatedEvent is published
TotpValidationResult check = auth.ValidateTotp("123456", secret, userId: "alice");

int secondsLeft = auth.GetSecondsUntilExpiry();
long window     = auth.GetCurrentTimeWindow();
```

### QrCodeGenerator

```csharp
var qr  = tfaLib.QrCode;
var key = tfaLib.GenerateNewKey("MyApp", "user@example.com");

Result<byte[]> png    = qr.GenerateQrCodePng(key);
Result<byte[]> bmp    = qr.GenerateQrCodeBmp(key);
Result<string> base64 = qr.GenerateQrCodeBase64(key);
Result<string> uri    = qr.GenerateQrCodeDataUri(key);
Result         saved  = qr.SaveQrCodeToFile(key, "qrcode.png");
```

## Configuration

Config file: `config.twofactorauth.json`

```json
{
  "Enabled": true,
  "TimeStepSeconds": 30,
  "WindowSize": 1,
  "QrCodeModuleSize": 20,
  "ErrorCorrectionLevel": "Q"
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Master switch; when `false` the services are not created and the health check reports *disabled* |
| `TimeStepSeconds` | `30` | TOTP time step in seconds (1–300); must match the authenticator app |
| `WindowSize` | `1` | Steps before/after the current window to accept (0–10); `1` ≈ ±30s drift tolerance |
| `QrCodeModuleSize` | `20` | Pixel size of each QR module (1–100) |
| `ErrorCorrectionLevel` | `Q` | QR error correction: `L` (~7%), `M` (~15%), `Q` (~25%), `H` (~30%) |

## Documentation

Full API docs: [https://github.com/Media2A/CodeLogic.Libs](https://github.com/Media2A/CodeLogic.Libs)

## Requirements

- .NET 10.0+
- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic)
- Otp.NET 1.x
- QRCoder 1.x

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
