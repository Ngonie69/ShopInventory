using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace ShopInventory.Web.Data;

/// <summary>
/// Builds a <see cref="WebAppDbContext"/> for EF Core tooling (migrations, `dotnet ef`, and the
/// migration bundle the production deploy runs) without going through <c>Program.Main</c>.
///
/// Without this, EF has to boot the whole web host just to reach the DbContext registration, which
/// drags in Serilog configuration, JWT secret validation and everything else the site needs at
/// runtime. That fails outside IIS - notably the single-file migration bundle, where Serilog cannot
/// discover its sink assemblies - and EF then has no fallback. Keep this factory free of app startup
/// concerns: configuration in, DbContext out.
///
/// The connection string is only a design-time default; `dotnet ef --connection` and the deploy
/// script's migration step both override it on the created context.
/// </summary>
public sealed class WebAppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<WebAppDbContext>
{
    public WebAppDbContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddUserSecrets<WebAppDbContextDesignTimeFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=ShopInventoryWeb;Username=postgres";

        var options = new DbContextOptionsBuilder<WebAppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new WebAppDbContext(options);
    }
}
