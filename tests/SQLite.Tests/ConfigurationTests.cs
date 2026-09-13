using CL.SQLite.Models;
using Xunit;

namespace SQLite.Tests;

/// <summary>
/// <see cref="SQLiteConfig"/> and <see cref="SQLiteDatabaseConfig"/> are POCOs with a
/// <c>Validate()</c> method; nothing here needs the runtime. Settings whose *effect* is
/// observable (WAL, foreign keys, pool size) are asserted in <see cref="ConnectionManagerTests"/>
/// rather than here — this file covers validation and defaults only.
/// </summary>
public sealed class ConfigurationTests
{
    [Fact]
    public void A_fresh_config_has_one_enabled_Default_database_and_validates()
    {
        var cfg = new SQLiteConfig();

        Assert.Single(cfg.Databases);
        Assert.True(cfg.Databases.ContainsKey("Default"));
        Assert.True(cfg.Validate().IsValid);
    }

    [Fact]
    public void Database_defaults_match_the_documented_values()
    {
        var db = new SQLiteDatabaseConfig();

        Assert.True(db.Enabled);
        Assert.Equal("database.db", db.DatabasePath);
        Assert.Equal(30u, db.ConnectionTimeoutSeconds);
        Assert.Equal(120u, db.CommandTimeoutSeconds);
        Assert.False(db.SkipTableSync);
        Assert.Equal(CacheMode.Default, db.CacheMode);
        Assert.True(db.UseWAL);
        Assert.True(db.EnableForeignKeys);
        Assert.Equal(10, db.MaxPoolSize);
        Assert.Equal(500, db.SlowQueryThresholdMs);
    }

    [Fact]
    public void An_empty_database_map_is_invalid()
    {
        var cfg = new SQLiteConfig { Databases = [] };

        var result = cfg.Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("At least one database", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_database_path_is_invalid(string path)
    {
        var db = new SQLiteDatabaseConfig { DatabasePath = path };

        var result = db.Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("DatabasePath", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_MaxPoolSize_is_invalid(int size)
    {
        var db = new SQLiteDatabaseConfig { MaxPoolSize = size };

        var result = db.Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("MaxPoolSize", StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_database_can_report_several_errors_at_once()
    {
        var db = new SQLiteDatabaseConfig { DatabasePath = "", MaxPoolSize = 0 };

        var result = db.Validate();
        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void The_top_level_Validate_names_the_offending_database_key()
    {
        var cfg = new SQLiteConfig
        {
            Databases = new Dictionary<string, SQLiteDatabaseConfig>
            {
                ["Good"] = new() { DatabasePath = "ok.db" },
                ["Bad"] = new() { DatabasePath = "" }
            }
        };

        var result = cfg.Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("'Bad'", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, e => e.Contains("'Good'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_disabled_database_is_still_validated()
    {
        // Validate() walks every entry regardless of Enabled — a disabled-but-broken entry
        // still fails the section. (SQLiteLibrary filters on Enabled *before* calling Validate,
        // so a disabled broken entry does not actually block startup.)
        var cfg = new SQLiteConfig
        {
            Databases = new Dictionary<string, SQLiteDatabaseConfig>
            {
                ["Off"] = new() { Enabled = false, DatabasePath = "" }
            }
        };

        Assert.False(cfg.Validate().IsValid);
    }

    [Fact]
    public void Several_valid_databases_validate_together()
    {
        var cfg = new SQLiteConfig
        {
            Databases = new Dictionary<string, SQLiteDatabaseConfig>
            {
                ["Default"] = new() { DatabasePath = "a.db" },
                ["Audit"] = new() { DatabasePath = "b.db", UseWAL = false, MaxPoolSize = 1 }
            }
        };

        Assert.True(cfg.Validate().IsValid);
    }
}
