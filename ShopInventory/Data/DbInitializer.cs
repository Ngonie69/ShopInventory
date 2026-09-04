using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

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
        await SeedVanSalesRoutesAsync(context, logger);

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
    /// Loads the published van sales schedule — the four upcountry routes and the four town trucks,
    /// with the areas each works — for any route or stop it has not placed before.
    /// </summary>
    /// <remarks>
    /// Insert-only, for the same reason <see cref="SeedItemVolumeConversionsAsync"/> is: the office
    /// maintains routes and stops through the app, so rewriting them on every start would undo their
    /// corrections on the next deploy and do it silently.
    /// <para>
    /// "Have I placed this before" is asked of <see cref="RouteStopEntity.SeedKey"/> — what the seed
    /// <em>put</em> on the row — and never of the row's current contents. That distinction is the
    /// whole mechanism. Matching on contents means an edit hides the row: rename Waterfalls and the
    /// next start cannot find Waterfalls, so it adds it back and Monday has both. Renaming and
    /// rescheduling are how this data is corrected, so that failure would have fired on almost every
    /// deploy, and quietly — nobody watches a start-up log for an extra INSERT.
    /// </para>
    /// <para>
    /// A stop the office has deactivated keeps its row and therefore its key, so it is not
    /// resurrected either: removing a stop is a decision, not a gap to be filled. A stop added to the
    /// seed list later has a key nothing carries, so it still arrives on the next start without a
    /// migration, which is the point of keeping the schedule as a list.
    /// </para>
    /// <para>
    /// The one thing insert-only cannot do by itself is un-place a stop, and editing an entry's text
    /// needs exactly that — the key is derived from the name, so an edit arrives as a new stop and
    /// leaves the old row behind. <see cref="VanSalesRouteSeedData.RetiredSeedKeys"/> names the keys
    /// that are no longer placed, and they are withdrawn here.
    /// </para>
    /// </remarks>
    internal static async Task SeedVanSalesRoutesAsync(ApplicationDbContext context, ILogger logger)
    {
        var seededRouteKeys = await context.Routes
            .AsNoTracking()
            .Where(route => route.SeedKey != null)
            .ToDictionaryAsync(route => route.SeedKey!, route => route.Id);

        var now = DateTime.UtcNow;
        var newRoutes = new List<RouteEntity>();

        foreach (var seed in VanSalesRouteSeedData.Routes)
        {
            if (seededRouteKeys.ContainsKey(seed.Code))
            {
                continue;
            }

            newRoutes.Add(new RouteEntity
            {
                Code = seed.Code,
                Name = seed.Name,
                Territory = seed.Territory,
                IsActive = true,
                SeedKey = seed.Code,
                CreatedAt = now
            });
        }

        if (newRoutes.Count > 0)
        {
            // A route this seeder has not placed may still be using the code, if somebody created it
            // by hand first. Taking the code from them would break the unique index and fail the
            // whole start-up, so the seeded route yields and takes a suffixed code; the office can
            // reconcile the two on the page. Its SeedKey is unchanged, so this happens once.
            var takenCodes = (await context.Routes
                    .AsNoTracking()
                    .Select(route => route.Code)
                    .ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var route in newRoutes)
            {
                if (!takenCodes.Add(route.Code))
                {
                    var suffixed = $"{route.Code}-SEED";
                    var attempt = 2;

                    while (!takenCodes.Add(suffixed))
                    {
                        suffixed = $"{route.Code}-SEED{attempt++}";
                    }

                    logger.LogWarning(
                        "Van sales route code {Code} is already taken; seeding it as {Suffixed}",
                        route.Code, suffixed);

                    route.Code = suffixed;
                }
            }

            await context.Routes.AddRangeAsync(newRoutes);

            // Saved before the stops so every route has its identity: a stop needs its RouteId, and
            // an unsaved route's is 0.
            await context.SaveChangesAsync();

            foreach (var route in newRoutes)
            {
                seededRouteKeys[route.SeedKey!] = route.Id;
            }

            logger.LogInformation("Seeded {Count} van sales route(s)", newRoutes.Count);
        }

        // Retire before placing, so that a stop split into two and then corrected back into one is
        // resolved within a single start rather than leaving all three on the page until the next.
        var retired = VanSalesRouteSeedData.RetiredSeedKeys.ToList();

        if (retired.Count > 0)
        {
            var withdrawn = await context.RouteStops
                .Where(stop => stop.IsActive && stop.SeedKey != null && retired.Contains(stop.SeedKey))
                .ToListAsync();

            if (withdrawn.Count > 0)
            {
                foreach (var stop in withdrawn)
                {
                    stop.IsActive = false;
                    stop.UpdatedAt = now;
                }

                await context.SaveChangesAsync();

                logger.LogInformation(
                    "Withdrew {Count} van sales route stop(s) the schedule no longer places: {Names}",
                    withdrawn.Count,
                    string.Join(", ", withdrawn.Select(stop => stop.Name)));
            }
        }

        var placedStopKeys = (await context.RouteStops
                .AsNoTracking()
                .Where(stop => stop.SeedKey != null)
                .Select(stop => stop.SeedKey!)
                .ToListAsync())
            .ToHashSet(StringComparer.Ordinal);

        var missingStops = new List<RouteStopEntity>();

        foreach (var seed in VanSalesRouteSeedData.Routes)
        {
            if (!seededRouteKeys.TryGetValue(seed.Code, out var routeId))
            {
                continue;
            }

            foreach (var stop in seed.Stops)
            {
                var key = VanSalesRouteSeedData.SeedKeyOf(seed.Code, stop);

                if (!placedStopKeys.Add(key))
                {
                    continue;
                }

                missingStops.Add(new RouteStopEntity
                {
                    RouteId = routeId,
                    Name = stop.Name,
                    DayOfWeek = stop.DayOfWeek,
                    WeekNumber = stop.WeekNumber,
                    AlternateSet = stop.AlternateSet,
                    Sequence = stop.Sequence,
                    IsActive = true,
                    SeedKey = key,
                    CreatedAt = now
                });
            }
        }

        if (missingStops.Count == 0)
        {
            return;
        }

        await context.RouteStops.AddRangeAsync(missingStops);
        await context.SaveChangesAsync();

        logger.LogInformation("Seeded {Count} van sales route stop(s)", missingStops.Count);
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
