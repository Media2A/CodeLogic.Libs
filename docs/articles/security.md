# Security & Two-Factor Auth — CL.TwoFactorAuth

CL.TwoFactorAuth provides TOTP (Time-based One-Time Password) two-factor authentication compatible with Google Authenticator, Authy, Microsoft Authenticator, and any RFC 6238-compliant app. Includes QR code generation for easy setup.

---

## Registration

```csharp
await Libraries.LoadAsync<CL.TwoFactorAuth.TwoFactorAuthLibrary>();
```

---

## Configuration (`config.twofactorauth.json`)

```json
{
  "Issuer": "MyApp",
  "SecretKeyLength": 20,
  "TokenValiditySeconds": 30,
  "AllowedClockSkewSeconds": 60,
  "QrCodeSize": 200
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Issuer` | required | App name shown in the authenticator |
| `SecretKeyLength` | `20` | Length of the TOTP secret in bytes |
| `TokenValiditySeconds` | `30` | TOTP window duration |
| `AllowedClockSkewSeconds` | `60` | Clock drift tolerance (±1 window by default) |
| `QrCodeSize` | `200` | QR code image size in pixels |

---

## Setting Up 2FA for a User

### Step 1: Generate a Secret

```csharp
var totp = context.GetLibrary<CL.TwoFactorAuth.TwoFactorAuthLibrary>();

// Generate a unique secret for this user
var secret = totp.GenerateSecret();   // returns base32-encoded string

// Store the secret in your database (associated with the user)
await userRepo.UpdateAsync(user with { TotpSecret = secret, TotpEnabled = false });
```

### Step 2: Generate a QR Code

```csharp
// Generate a QR code image the user scans with their authenticator app
byte[] qrCodePng = totp.GenerateQrCode(
    userEmail: "alice@example.com",
    secret:    secret
);

// Return as base64 data URI for embedding in HTML
string dataUri = $"data:image/png;base64,{Convert.ToBase64String(qrCodePng)}";

// Or save to a file / return as a file download
await File.WriteAllBytesAsync("qrcode.png", qrCodePng);
```

The QR code encodes a `otpauth://` URI:
```
otpauth://totp/MyApp:alice@example.com?secret=BASE32SECRET&issuer=MyApp
```

### Step 3: Verify the First Token (Activate 2FA)

Ask the user to enter the 6-digit code from their authenticator app to confirm setup:

```csharp
bool isValid = totp.Validate(secret: user.TotpSecret, token: userEnteredCode);

if (isValid)
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
    bool valid = totp.Validate(
        secret: user.TotpSecret,
        token:  request.TotpCode   // 6-digit code from the app
    );

    if (!valid)
        return Unauthorized("Invalid two-factor authentication code");
}

// Proceed with login
return Ok(GenerateJwt(user));
```

---

## Manual URI (for testing)

```csharp
// Get the raw otpauth:// URI without generating a QR image
string uri = totp.GetOtpAuthUri(
    userEmail: "alice@example.com",
    secret:    secret
);
// otpauth://totp/MyApp:alice@example.com?secret=JBSWY3DPEHPK3PXP&issuer=MyApp
```

---

## Backup Codes

CL.TwoFactorAuth can generate single-use backup codes for account recovery:

```csharp
// Generate 10 backup codes
string[] backupCodes = totp.GenerateBackupCodes(count: 10);
// ["ABCD-EFGH", "IJKL-MNOP", ...]

// Store hashed codes in database
var hashedCodes = backupCodes.Select(c => totp.HashBackupCode(c)).ToArray();
await userRepo.UpdateAsync(user with { BackupCodes = hashedCodes });

// Show plain codes to the user once — they won't be shown again

// At login, verify a backup code
bool usedCode = totp.VerifyBackupCode(
    enteredCode:  request.BackupCode,
    hashedCodes:  user.BackupCodes,
    out string[]  remainingCodes  // codes with the used one removed
);

if (usedCode)
{
    // Remove the used code
    await userRepo.UpdateAsync(user with { BackupCodes = remainingCodes });
    // Proceed with login
}
```

---

## Health Check

```csharp
// Always returns Healthy (library is stateless)
var status = await totp.HealthCheckAsync();
```
