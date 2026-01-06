using Microsoft.EntityFrameworkCore;
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
    public static async Task InitializeAsync(ApplicationDbContext context, ILogger logger)
    {
        // Ensure database is created and migrations are applied
        await context.Database.MigrateAsync();

        // Check if we already have users
        if (await context.Users.AnyAsync())
        {
            logger.LogInformation("Database already seeded with users");
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
                Role = "Admin",
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
                Role = "User",
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
                Role = "ApiUser",
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
}
