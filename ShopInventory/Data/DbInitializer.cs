using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using ShopInventory.Models;

namespace ShopInventory.Data;

/// <summary>
/// Database initializer for seeding initial data
/// </summary>
public static class DbInitializer
{
    /// <summary>
    /// Initialize the database with seed data
    /// </summary>
    public static async Task InitializeAsync(ApplicationDbContext context, ILogger logger, IWebHostEnvironment environment)
    {
        // Ensure database is created and migrations are applied
        // Skip migration if there are pending model changes (dev mode)
        try
        {
            await context.Database.MigrateAsync();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("PendingModelChanges"))
        {
            logger.LogWarning("Pending model changes detected, skipping automatic migration. Run 'dotnet ef migrations add' manually.");
            // Ensure the database exists at least
            await context.Database.EnsureCreatedAsync();
        }

        await SeedItemVolumeConversionsAsync(context, logger);

        // Check if we already have users
        if (await context.Users.AnyAsync())
        {
            logger.LogInformation("Database already seeded with users");
            return;
        }

        if (!environment.IsDevelopment())
        {
            logger.LogWarning("Database contains no users. Default credentials are seeded only in Development.");
            return;
        }

        logger.LogInformation("Seeding database with initial users...");

        // Create default users with BCrypt hashed passwords
        var users = new List<User>
        {
            new User
            {
                Id = Guid.NewGuid(),
                Username = "admin",
                Email = "admin@shopinventory.local",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin123", workFactor: 12),
                Role = ApplicationRoles.Admin,
                FirstName = "System",
                LastName = "Administrator",
                IsActive = true,
                EmailVerified = true,
                CreatedAt = DateTime.UtcNow
            },
            new User
            {
                Id = Guid.NewGuid(),
                Username = "user",
                Email = "user@shopinventory.local",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("user123", workFactor: 12),
                Role = ApplicationRoles.Cashier,
                FirstName = "Standard",
                LastName = "User",
                IsActive = true,
                EmailVerified = true,
                CreatedAt = DateTime.UtcNow
            },
            new User
            {
                Id = Guid.NewGuid(),
                Username = "api",
                Email = "api@shopinventory.local",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("api123", workFactor: 12),
                Role = ApplicationRoles.ApiUser,
                FirstName = "API",
                LastName = "Service Account",
                IsActive = true,
                EmailVerified = true,
                CreatedAt = DateTime.UtcNow
            }
        };

        await context.Users.AddRangeAsync(users);
        await context.SaveChangesAsync();

        logger.LogInformation("Database seeded with {Count} users", users.Count);
    }

    /// <summary>
    /// Loads the supplied volume conversion factors for any item code that does not have one yet.
    /// </summary>
    /// <remarks>
    /// Insert-only on purpose. Administrators maintain these factors through the app, so rewriting a
    /// row on every start would quietly undo their corrections; and because it only fills gaps, a
    /// code added to the seed list later arrives on the next start without a migration.
    /// </remarks>
    private static async Task SeedItemVolumeConversionsAsync(ApplicationDbContext context, ILogger logger)
    {
        var existingCodes = await context.ItemVolumeConversions
            .AsNoTracking()
            .Select(conversion => conversion.ItemCode)
            .ToListAsync();

        var existingCodeSet = existingCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = ItemVolumeConversionSeedData
            .BuildEntities(DateTime.UtcNow)
            .Where(conversion => !existingCodeSet.Contains(conversion.ItemCode))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        await context.ItemVolumeConversions.AddRangeAsync(missing);
        await context.SaveChangesAsync();

        logger.LogInformation("Seeded {Count} item volume conversion factor(s)", missing.Count);
    }
}
