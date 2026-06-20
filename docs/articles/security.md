# Security & Two-Factor Auth — CL.TwoFactorAuth

CL.TwoFactorAuth provides TOTP (Time-based One-Time Password) two-factor authentication compatible with Google Authenticator, Authy, Microsoft Authenticator, 1Password, and any RFC 6238-compliant app. It includes QR code generation for easy setup.

---

## Registration

```csharp
await Libraries.LoadAsync<CL.TwoFactorAuth.TwoFactorAuthLibrary>();
```

---

## Configuration (`config.twofactorauth.json`)

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
| `Enabled` | `true` | Master switch. When `false`, the services are not created and the health check reports *disabled*. |
| `TimeStepSeconds` | `30` | TOTP time step in seconds (range 1–300). Must match the authenticator app — standard is 30. |
| `WindowSize` | `1` | Number of steps before/after the current window to accept (range 0–10). `1` ≈ ±30 s clock-drift tolerance. |
| `QrCodeModuleSize` | `20` | Pixel size of each QR module (range 1–100). Larger values produce a bigger image. |
| `ErrorCorrectionLevel` | `Q` | QR error correction: `L` (~7 %), `M` (~15 %), `Q` (~25 %), `H` (~30 % damage tolerance). |

---

## Setting Up 2FA for a User

### Step 1: Generate a Key

`GenerateNewKey` creates a fresh Base32 secret bound to an issuer and user and returns a `TwoFactorKey`. (Use `GenerateSecretKey()` if you only need the raw secret string.)

```csharp
var totp = context.GetLibrary<CL.TwoFactorAuth.TwoFactorAuthLibrary>();

// Generate a unique key for this user
TwoFactorKey key = totp.GenerateNewKey("MyApp", "alice@example.com");

// Store key.SecretKey in your database (associated with the user)
await userRepo.UpdateAsync(user with { TotpSecret = key.SecretKey, TotpEnabled = false });
```

### Step 2: Generate a QR Code

The QR generator works from a `TwoFactorKey` and returns a `Result<T>`. A data URI is the simplest way to embed the image in HTML:

```csharp
// Data URI for embedding directly in an <img> tag
Result<string> dataUri = totp.GenerateQrCodeDataUri(key);
if (dataUri.IsSuccess)
{
    var html = $"<img src=\"{dataUri.Value}\" alt=\"Scan with your authenticator app\" />";
}
```

Other output formats are available on the `QrCode` service:

```csharp
Result<byte[]> png    = totp.QrCode.GenerateQrCodePng(key);    // raw PNG bytes
Result<byte[]> bmp    = totp.QrCode.GenerateQrCodeBmp(key);    // raw BMP bytes
Result<string> base64 = totp.QrCode.GenerateQrCodeBase64(key); // base64 PNG
Result         saved  = totp.QrCode.SaveQrCodeToFile(key, "qrcode.png");
```

The QR code encodes the key's `otpauth://` provisioning URI:

```
otpauth://totp/MyApp:alice@example.com?secret=BASE32SECRET&issuer=MyApp
```

### Step 3: Verify the First Code (Activate 2FA)

Ask the user to enter the 6-digit code from their authenticator app to confirm setup. `ValidateTotp` returns a `TotpValidationResult`:

```csharp
TotpValidationResult result = totp.ValidateTotp(userEnteredCode, user.TotpSecret);

if (result.IsValid)
{
    // Activate 2FA for this user
    await userRepo.UpdateAsync(user with { TotpEnabled = true });
    return Ok("Two-factor authentication enabled");
}
else
{
    return BadRequest("Invalid code. Please try again.");
}
```

---

## Validating 2FA at Login

```csharp
// After the user enters their password and provides a 2FA code:
if (user.TotpEnabled)
{
    // Pass the optional userId to publish a TotpValidatedEvent on the event bus
    TotpValidationResult result = totp.Authenticator.ValidateTotp(
        code:      request.TotpCode,   // 6-digit code from the app
        secretKey: user.TotpSecret,
        userId:    user.Id);

    if (!result.IsValid)
        return Unauthorized("Invalid two-factor authentication code");
}

// Proceed with login
return Ok(GenerateJwt(user));
```

`TotpValidationResult` carries:

- `IsValid` — whether the code matched.
- `MatchedWindow` — the matched time-window offset (`0` = current window, negative/positive = past/future), or `null` on failure.
- `ErrorMessage` — a human-readable reason when validation fails.

---

## Provisioning URI (for testing)

The `otpauth://` URI is exposed directly on the key — no QR rendering required:

```csharp
TwoFactorKey key = totp.GenerateNewKey("MyApp", "alice@example.com");
string uri = key.ProvisioningUri;
// otpauth://totp/MyApp:alice@example.com?secret=JBSWY3DPEHPK3PXP&issuer=MyApp
```

---

## Code Lifetime Helpers

```csharp
var auth = totp.Authenticator;

// Generate the current code for a secret (e.g. for tests)
Result<string> code = auth.GenerateTotpCode(secret);

// Seconds until the current code rolls over
int secondsLeft = auth.GetSecondsUntilExpiry();

// Current TOTP time window (Unix epoch / step size)
long window = auth.GetCurrentTimeWindow();
```

---

## Events

When wired to the CodeLogic event bus, the library publishes:

| Event | Published when |
|-------|----------------|
| `SecretKeyGeneratedEvent(IssuerName, UserName, GeneratedAt)` | `GenerateNewKey` creates a key |
| `TotpValidatedEvent(UserId, IsValid, MatchedWindow, ValidatedAt)` | `ValidateTotp` is called **with** a `userId` |

---

## Health Check

```csharp
var status = await totp.HealthCheckAsync();
```

- Reports **Healthy** ("disabled") when the library is disabled in configuration.
- Reports **Unhealthy** ("Not initialized") if the services were never created.
- Otherwise exercises live TOTP key generation and reports **Healthy** / **Unhealthy** based on the outcome.
