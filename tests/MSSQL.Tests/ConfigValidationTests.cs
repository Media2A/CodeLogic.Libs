using CL.MSSQL.Configuration;
using Xunit;

namespace MSSQL.Tests;

// Configuration validation and connection-string building: the first thing a
// misconfigured deployment hits, and previously unexercised.

public sealed class ConfigValidationTests
{
    private static SqlServerDatabaseConfig Valid() => new()
    {
        Host = "localhost", Port = 1433, Database = "app", Username = "u", Password = "p",
    };

    [Fact]
    public void A_complete_config_validates()
    {
        Assert.True(Valid().Validate().IsValid);
    }

    [Fact]
    public void A_disabled_database_skips_validation_entirely()
    {
        var cfg = new SqlServerDatabaseConfig { Enabled = false };   // no database name
        Assert.True(cfg.Validate().IsValid);
    }

    [Fact]
    public void An_explicit_connection_string_bypasses_the_structured_checks()
    {
        var cfg = new SqlServerDatabaseConfig
        {
            ConnectionString = "Server=x;Database=y;Integrated Security=True",
            Host = "", Database = "",
        };
        Assert.True(cfg.Validate().IsValid);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Database")]
    [InlineData("Username")]
    [InlineData("DefaultSchema")]
    public void A_missing_required_field_is_reported_by_name(string field)
    {
        var cfg = Valid();
        switch (field)
        {
            case "Host": cfg.Host = "  "; break;
            case "Database": cfg.Database = ""; break;
            case "Username": cfg.Username = ""; break;
            case "DefaultSchema": cfg.DefaultSchema = ""; break;
        }

        var result = cfg.Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Integrated_security_does_not_require_a_username()
    {
        var cfg = Valid();
        cfg.AuthenticationMode = SqlServerAuthenticationMode.IntegratedSecurity;
        cfg.Username = "";
        Assert.True(cfg.Validate().IsValid);
    }

    [Fact]
    public void An_inverted_pool_range_is_rejected()
    {
        var cfg = Valid();
        cfg.MinPoolSize = 50;
        cfg.MaxPoolSize = 10;
        Assert.False(cfg.Validate().IsValid);
    }

    [Theory]
    [InlineData(-1, 30, 3, 100)]      // negative connection timeout
    [InlineData(30, -1, 3, 100)]      // negative command timeout
    [InlineData(30, 30, 999, 100)]    // retry count out of range
    [InlineData(30, 30, 3, 0)]        // zero batch size
    public void Out_of_range_tuning_values_are_rejected(
        int connectionTimeout, int commandTimeout, int retries, int batchSize)
    {
        var cfg = Valid();
        cfg.ConnectionTimeout = connectionTimeout;
        cfg.CommandTimeout = commandTimeout;
        cfg.TransientRetryCount = retries;
        cfg.MaxBatchInsertSize = batchSize;
        Assert.False(cfg.Validate().IsValid);
    }

    [Fact]
    public void Special_characters_are_quoted_not_injected()
    {
        // Built through SqlConnectionStringBuilder: a ';' in a value must not be able to
        // append a new connection option.
        var cfg = Valid();
        cfg.Password = "pa;ss=word'\"x";

        var parsed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cfg.BuildConnectionString());
        Assert.Equal("pa;ss=word'\"x", parsed.Password);
        Assert.Equal("app", parsed.InitialCatalog);
    }

    [Fact]
    public void The_top_level_config_aggregates_per_database_errors()
    {
        var cfg = new DatabaseConfiguration();   // the default "Default" entry is incomplete
        var result = cfg.Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Default", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_databases_at_all_is_invalid()
    {
        Assert.False(new DatabaseConfiguration { Databases = new() }.Validate().IsValid);
    }
}
