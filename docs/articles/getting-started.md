# Getting Started with CL.* Libraries

This guide explains how to reference a `CL.*` library, register it with the CodeLogic 3 framework, configure it, and use it from your application.

---

## Prerequisites

- [CodeLogic 3 framework](https://media2a.github.io/CodeLogic) set up in your project
- .NET 10 SDK

---

## Step 1: Reference the Library

Add a project reference to the desired `CL.*` library:

```xml
<!-- YourApp.csproj -->
<ItemGroup>
  <!-- Reference only what you need -->
  <ProjectReference Include="path/to/CodeLogic.Libs/CL.SQLite/CL.SQLite.csproj" />
  <ProjectReference Include="path/to/CodeLogic.Libs/CL.Mail/CL.Mail.csproj" />
</ItemGroup>
```

---

## Step 2: Register the Library

Register it in `Program.cs` before `ConfigureAsync()`:

```csharp
using CodeLogic;

var result = await CodeLogic.InitializeAsync(o => { o.AppVersion = "1.0.0"; });
if (result.ShouldExit) return;

// Register CL.* libraries
await Libraries.LoadAsync<CL.SQLite.SQLiteLibrary>();
await Libraries.LoadAsync<CL.Mail.MailLibrary>();

CodeLogic.RegisterApplication(new MyApp());
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();
```

---

## Step 3: Configure It

On first run, CodeLogic generates a config file for each library in its directory:

```
CodeLogic/
  Libraries/
    CL.SQLite/
      config.sqlite.json      ← edit this
    CL.Mail/
      config.mail.json        ← edit this
```

Edit the generated files with your settings, then restart.

Example `config.sqlite.json`:

```json
{
  "DatabasePath": "data/myapp.db",
  "MaxConnections": 10,
  "TimeoutSeconds": 30,
  "EnableWAL": true
}
```

---

## Step 4: Use It

Access the library from your application or other libraries via the context:

```csharp
public class MyApp : IApplication
{
    public ApplicationManifest Manifest => new()
    {
        Id = "MyApp", Name = "My Application", Version = "1.0.0"
    };

    public Task OnConfigureAsync(ApplicationContext context) => Task.CompletedTask;

    public async Task RunAsync(ApplicationContext context)
    {
        // Get a typed repository from SQLite
        var sqlite = context.GetLibrary<CL.SQLite.SQLiteLibrary>();
        var userRepo = sqlite.GetRepository<User>();

        var user = await userRepo.FindAsync(u => u.Email == "alice@example.com");
        context.Logger.LogInformation("Found user: {Name}", user?.Name);

        await Task.Delay(Timeout.Infinite, context.CancellationToken);
    }

    public Task<HealthStatus> HealthCheckAsync()
        => Task.FromResult(HealthStatus.Healthy("Running"));
}
```

---

## Using a Library from Another Library

Libraries can depend on each other. Declare the dependency in `LibraryManifest`:

```csharp
public LibraryManifest Manifest => new()
{
    Id           = "MyApp.Reports",
    Name         = "Reports Library",
    Version      = "1.0.0",
    Dependencies =
    [
        new LibraryDependency("CL.SQLite", "2.0.0"),
        new LibraryDependency("CL.Mail", "2.0.0")
    ]
};

public async Task OnInitializeAsync(LibraryContext context)
{
    var sqlite = context.GetLibrary<CL.SQLite.SQLiteLibrary>();
    _reportRepo = sqlite.GetRepository<Report>();
}
```

---

## Library Directory Layout

Each library gets its own isolated directory:

```
CodeLogic/
  Libraries/
    CL.SQLite/
      config.sqlite.json
      logs/
        CL.SQLite-2026-04-01.log
      data/
        myapp.db
    CL.Mail/
      config.mail.json
      logs/
```

Libraries never share directories or config files.

---

## Health Checks

All CL.* libraries implement `HealthCheckAsync()`. View health from the CLI:

```bash
./MyApp --health
```

Or programmatically:

```csharp
var report = await CodeLogic.GetHealthAsync();
Console.WriteLine(report.ToConsoleString());
```

Example output:
```
Overall: Healthy

  CL.SQLite       Healthy    Connected (3/10 connections) [1ms]
  CL.Mail         Healthy    SMTP ready [smtp.example.com:587]
  MyApp           Healthy    Running
```

---

## What's Next

- [Database Libraries](database-libraries.md) — CL.MySQL2, CL.PostgreSQL, CL.SQLite with Repository pattern
- [Mail](mail.md) — sending email with templates
- [Storage](storage.md) — Amazon S3 and MinIO
- [Security](security.md) — TOTP two-factor authentication
- [System Monitoring](system-monitoring.md) — CPU and memory stats
- [Social](social.md) — Discord webhooks and Steam auth
