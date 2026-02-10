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
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<WebAppDbContext>>();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        await ApplyMigrationsAsync(dbContext);
        await SeedDefaultSettingsAsync(serviceProvider);
    }

    private static async Task ApplyMigrationsAsync(WebAppDbContext dbContext)
    {
        try
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
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to apply database migrations");
            throw;
        }
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
