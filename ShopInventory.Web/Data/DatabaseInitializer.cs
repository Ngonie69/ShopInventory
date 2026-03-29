using Microsoft.EntityFrameworkCore;
using Serilog;

namespace ShopInventory.Web.Data;

/// <summary>
/// Handles database initialization using EF Core migrations and seeding.
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// Applies pending migrations and seeds default data.
    /// The app will continue to start even if the database is temporarily unreachable.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        try
        {
            var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<WebAppDbContext>>();
            await using var dbContext = await dbContextFactory.CreateDbContextAsync();

            await ApplyMigrationsAsync(dbContext);
            await SeedDefaultSettingsAsync(serviceProvider);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Database initialization failed — the app will start without database access. " +
                "Fix the connection string or database server, then restart the app");
        }
    }

    private static async Task ApplyMigrationsAsync(WebAppDbContext dbContext)
    {
        var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
        var pendingList = pendingMigrations.ToList();

        if (pendingList.Count > 0)
        {
            Log.Information("Applying {Count} pending migrations: {Migrations}",
                pendingList.Count, string.Join(", ", pendingList));
        }

        await dbContext.Database.MigrateAsync();
        Log.Information("Database migrations applied successfully");
    }

    private static async Task SeedDefaultSettingsAsync(IServiceProvider serviceProvider)
    {
        try
        {
            var appSettingsService = serviceProvider.GetRequiredService<Services.IAppSettingsService>();
            await appSettingsService.InitializeDefaultSettingsAsync();
            Log.Information("Default settings initialized successfully");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not initialize default settings");
        }
    }
}
