using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using ApiPolicy = ShopInventory.Configuration.PostgresConnectionPolicyOptions;
using ApiValidator = ShopInventory.Configuration.PostgresConnectionStringValidator;
using WebPolicy = ShopInventory.Web.Configuration.PostgresConnectionPolicyOptions;
using WebValidator = ShopInventory.Web.Configuration.PostgresConnectionStringValidator;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the pool caps the API and the Web apply to their connection string at startup.
/// </summary>
/// <remarks>
/// Production's string lives in each slot's web.config and asked for 100 connections and 10 idle per
/// process, against a server limit of 200 shared by every process. The caps come from appsettings.json so
/// a release can lower them without touching the server.
/// </remarks>
public class PostgresConnectionPoolCapTests
{
    private const string ProductionShape =
        "Host=10.10.10.9,10.10.10.58;Port=5432;Database=ShopInventory;Username=postgres;Password=x;" +
        "Target Session Attributes=read-write;Maximum Pool Size=100;Minimum Pool Size=10";

    public static TheoryData<string> Apps => new() { "api", "web" };

    [Theory]
    [MemberData(nameof(Apps))]
    public void A_string_above_the_caps_is_lowered_to_them(string app)
    {
        var result = new NpgsqlConnectionStringBuilder(Validate(app, ProductionShape, maximum: 40, minimum: 2));

        Assert.Equal(40, result.MaxPoolSize);
        Assert.Equal(2, result.MinPoolSize);
        // Everything else survives the rewrite.
        Assert.Equal("10.10.10.9,10.10.10.58", result.Host);
        Assert.Equal("x", result.Password);
        Assert.Equal("read-write", result.TargetSessionAttributes, ignoreCase: true);
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public void A_string_inside_the_caps_comes_back_unchanged(string app)
    {
        const string small = "Host=db;Database=ShopInventory;Username=u;Password=x;Maximum Pool Size=20;Minimum Pool Size=0";

        Assert.Same(small, Validate(app, small, maximum: 40, minimum: 2));
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public void No_caps_configured_leaves_the_string_alone(string app)
    {
        Assert.Same(ProductionShape, Validate(app, ProductionShape, maximum: null, minimum: null));
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public void The_idle_minimum_never_exceeds_a_lowered_maximum(string app)
    {
        var result = new NpgsqlConnectionStringBuilder(Validate(app, ProductionShape, maximum: 5, minimum: null));

        Assert.Equal(5, result.MaxPoolSize);
        Assert.Equal(5, result.MinPoolSize);
    }

    private static string Validate(string app, string connectionString, int? maximum, int? minimum) =>
        app == "api"
            ? ApiValidator.Validate(
                connectionString,
                new Environment(),
                new ApiPolicy { MaximumPoolSize = maximum, MinimumPoolSize = minimum },
                "DefaultConnection")
            : WebValidator.Validate(
                connectionString,
                new Environment(),
                new WebPolicy { MaximumPoolSize = maximum, MinimumPoolSize = minimum },
                "DefaultConnection");

    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "ShopInventory.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
