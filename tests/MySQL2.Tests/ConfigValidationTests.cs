using CL.MySQL2.Configuration;
using Xunit;

namespace MySQL2.Tests;

// Configuration validation and connection-string building: the first thing a
// misconfigured deployment hits, and previously unexercised.

public sealed class ConfigValidationTests
{
    private static MySqlDatabaseConfig Valid() => new()
    {
        Host = "localhost", Port = 3306, Database = "app", Username = "u", Password = "p",
    };

    [Fact]
    public void A_complete_config_validates()
    {
        Assert.True(Valid().Validate().IsValid);
    }

    [Fact]
    public void A_disabled_database_skips_validation_entirely()
    {
        var cfg = new MySqlDatabaseConfig { Enabled = false };   // no host, no database
        Assert.True(cfg.Validate().IsValid);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Database")]
    [InlineData("Username")]
    public void A_missing_required_field_is_reported_by_name(string field)
    {
        var cfg = Valid();
        switch (field)
        {
            case "Host": cfg.Host = "  "; break;
            case "Database": cfg.Database = ""; break;
            case "Username": cfg.Username = ""; break;
        }

        var result = cfg.Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_inverted_pool_range_is_rejected()
    {
        var cfg = Valid();
        cfg.MinPoolSize = 50;
        cfg.MaxPoolSize = 10;
        Assert.False(cfg.Validate().IsValid);
    }

    [Fact]
    public void Special_characters_are_quoted_not_injected()
    {
        // Built through MySqlConnectionStringBuilder: a ';' in a value must not be able to
        // append a new connection option.
        var cfg = Valid();
        cfg.Password = "pa;ss=word'\"x";

        var parsed = new MySqlConnector.MySqlConnectionStringBuilder(cfg.BuildConnectionString());
        Assert.Equal("pa;ss=word'\"x", parsed.Password);
        Assert.Equal("app", parsed.Database);
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
