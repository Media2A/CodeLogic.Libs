# CL.TwoFactorAuth

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.TwoFactorAuth)](https://www.nuget.org/packages/CodeLogic.TwoFactorAuth)

TOTP two-factor authentication with QR code generation for CodeLogic 3 applications.

## Install

```
dotnet add package CodeLogic.TwoFactorAuth
```

## Quick Start

```csharp
var tfaLib = new TwoFactorAuthLibrary();
// After library initialization via CodeLogic framework:

// Generate a new key for a user
var key = tfaLib.GenerateNewKey("MyApp", "user@example.com");

// Render a QR code for Google Authenticator / Authy
var qrDataUri = tfaLib.GenerateQrCodeDataUri(key);
// Use qrDataUri.Value in an <img src="..."> tag

// Validate a code entered by the user
var result = tfaLib.ValidateTotp("123456", key.Secret);
Console.WriteLine($"Valid: {result.IsValid}");
```

## Features

- **Secret key generation** — cryptographically random Base32-encoded TOTP secrets
- **TOTP validation** — verify 6-digit codes with configurable time-step and drift window
- **QR code rendering** — PNG, BMP, Base64, and data URI output for authenticator app provisioning
- **Google Authenticator compatible** — standard `otpauth://` URI format
- **Event integration** — validation attempts are published to the CodeLogic event bus

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

## Documentation

Full API docs: [https://github.com/Media2A/CodeLogic.Libs](https://github.com/Media2A/CodeLogic.Libs)

## Requirements

- .NET 10.0+
- CodeLogic 3.0.0+
- Otp.NET 1.x
- QRCoder 1.x

## License

MIT -- see [LICENSE](../LICENSE) for details.
