using ErrorOr;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Commands.DeleteRouteStop;
using ShopInventory.Features.VanSalesReports.Commands.ReorderRouteStops;
using ShopInventory.Features.VanSalesReports.Commands.SaveRouteStop;
using ShopInventory.Features.VanSalesReports.Queries.GetRouteStops;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the published van sales schedule: the seed list itself, the seeder that applies it, and
/// the handlers that let the office correct it afterwards.
///
/// Two things here are worth more than the rest. The first is that the seeder is genuinely
/// insert-only and genuinely idempotent — it runs on every application start, so a bug that
/// duplicates a stop duplicates it once per deploy and a bug that overwrites one undoes the office's
/// corrections on a schedule nobody is watching. The second is the NULL trap: a stop's identity spans
/// two nullable columns, and in SQL <c>NULL = NULL</c> is not true, so the obvious duplicate check
/// silently passes for every upcountry stop. Both the seeder and the save handler compare in memory
/// instead, and the cases below fail if either is moved back into a query.
/// </summary>
public sealed class VanSalesRouteScheduleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesRouteScheduleTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- The seed list ---

    /// <summary>
    /// The schedule as published: four upcountry routes and four town trucks, and the stop counts
    /// the source lists for each.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than derived, so that this fails when the seed list changes. That is the
    /// point of it — the counts are the one thing a careless edit to a long literal list gets wrong
    /// without looking wrong, and the source they came from is quoted verbatim in the seed file.
    /// </remarks>
    [Theory]
    [InlineData("UPC1", "Upc 1", "UPC", 8)]
    [InlineData("UPC2", "Upc 2", "UPC", 11)]
    [InlineData("UPC3", "Upc 3", "UPC", 12)]
    [InlineData("UPC4", "Upc 4", "UPC", 11)]
    [InlineData("EAST", "East Truck", "Harare", 13)]
    [InlineData("WEST1", "West 1 Truck", "Harare", 9)]
    [InlineData("WEST2", "West 2 Truck", "Harare", 9)]
    [InlineData("CBDCZA", "CBD/CZA Truck", "Harare", 11)]
    public void SeedList_HasTheRouteAndItsStopCount(string code, string name, string territory, int stops)
    {
        var route = VanSalesRouteSeedData.Routes.Single(r => r.Code == code);

        Assert.Equal(name, route.Name);
        Assert.Equal(territory, route.Territory);
        Assert.Equal(stops, route.Stops.Count);
    }

    [Fact]
    public void SeedList_HasEightRoutesAndNothingElse()
    {
        Assert.Equal(8, VanSalesRouteSeedData.Routes.Count);
        Assert.Equal(84, VanSalesRouteSeedData.Routes.Sum(route => route.Stops.Count));
    }

    /// <summary>
    /// Every code is unique and every stop is named. The code is what a report groups on and what the
    /// seeder matches on, so a duplicate would silently merge two rounds into one.
    /// </summary>
    [Fact]
    public void SeedList_CodesAreUniqueAndEveryStopIsNamed()
    {
        var codes = VanSalesRouteSeedData.Routes.Select(route => route.Code).ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.All(
            VanSalesRouteSeedData.Routes.SelectMany(route => route.Stops),
            stop => Assert.False(string.IsNullOrWhiteSpace(stop.Name)));
    }

    /// <summary>
    /// An upcountry route commits to a cycle week and never to a weekday. Writing a day there would
    /// be an invention, and every coverage report would then take it literally.
    /// </summary>
    [Fact]
    public void UpcountryRoutes_AreScheduledByWeekAndNotByWeekday()
    {
        var upcountry = VanSalesRouteSeedData.Routes
            .Where(route => route.Territory == VanSalesRouteSeedData.UpcountryTerritory)
            .ToList();

        Assert.Equal(4, upcountry.Count);

        Assert.All(upcountry.SelectMany(route => route.Stops), stop =>
        {
            Assert.Null(stop.DayOfWeek);
            Assert.Contains(stop.WeekNumber, new int?[] { 1, 2 });
        });

        // Both weeks are populated on every one of them, which is what makes the route fortnightly
        // rather than a weekly round with a stray second list.
        Assert.All(upcountry, route =>
        {
            Assert.Contains(route.Stops, stop => stop.WeekNumber == 1);
            Assert.Contains(route.Stops, stop => stop.WeekNumber == 2);
        });
    }

    /// <summary>
    /// A town truck repeats weekly, so it has a weekday and no cycle week. Null is not week 1 — a
    /// 1 here would be indistinguishable from the first week of a fortnightly upcountry run.
    /// </summary>
    [Fact]
    public void TownTrucks_AreScheduledByWeekdayAndKeepNoCycleWeek()
    {
        var trucks = VanSalesRouteSeedData.Routes
            .Where(route => route.Territory == VanSalesRouteSeedData.TownTerritory)
            .ToList();

        Assert.Equal(4, trucks.Count);

        Assert.All(trucks.SelectMany(route => route.Stops), stop =>
        {
            Assert.NotNull(stop.DayOfWeek);
            Assert.Null(stop.WeekNumber);

            // Nobody's round runs at the weekend, and a Sunday would sort ahead of Monday because
            // DayOfWeek numbers Sunday 0 — the schedule would read as starting on the wrong day.
            Assert.InRange(stop.DayOfWeek!.Value, DayOfWeek.Monday, DayOfWeek.Friday);
        });
    }

    /// <summary>
    /// Upc 1's two weeks are the two halves the source gives, in order.
    /// </summary>
    /// <remarks>
    /// One route checked stop by stop rather than all four, because what this is really pinning is
    /// the reading of the source line — that "Chiredzi Masvingo", written without a comma, is the two
    /// towns and not one stop belonging to neither.
    /// </remarks>
    [Fact]
    public void Upc1_RunsSouthOneWeekAndNorthTheNext()
    {
        var route = VanSalesRouteSeedData.Routes.Single(r => r.Code == "UPC1");

        Assert.Equal(
            new[] { "Gutu", "Nyika", "Chiredzi", "Masvingo" },
            route.Stops.Where(stop => stop.WeekNumber == 1).OrderBy(stop => stop.Sequence).Select(stop => stop.Name));

        Assert.Equal(
            new[] { "Mvurwi", "Guruve", "Mt Darwin", "Madziva" },
            route.Stops.Where(stop => stop.WeekNumber == 2).OrderBy(stop => stop.Sequence).Select(stop => stop.Name));
    }

    /// <summary>
    /// West 2's Wednesday is published as two alternatives — Dzivarasekwa and Whitehouse, <em>or</em>
    /// Hatcliff and Mungate — and both are the plan.
    /// </summary>
    /// <remarks>
    /// Merging them into one four-stop Wednesday is the mistake this guards: it would double the
    /// day's planned coverage and make a van that worked the round exactly as published look like it
    /// missed half of it.
    /// </remarks>
    [Fact]
    public void West2_PublishesTwoAlternativesForWednesday()
    {
        var wednesday = VanSalesRouteSeedData.Routes
            .Single(route => route.Code == "WEST2")
            .Stops
            .Where(stop => stop.DayOfWeek == DayOfWeek.Wednesday)
            .ToList();

        Assert.Equal(
            new[] { "Dzivarasekwa", "Whitehouse" },
            wednesday.Where(stop => stop.AlternateSet == 0).OrderBy(stop => stop.Sequence).Select(stop => stop.Name));

        Assert.Equal(
            new[] { "Hatcliff", "Mungate" },
            wednesday.Where(stop => stop.AlternateSet == 1).OrderBy(stop => stop.Sequence).Select(stop => stop.Name));

        // Each set is numbered from 1. An alternative that continued the standard set's numbering
        // would read as a fifth and sixth stop of the same day rather than as a replacement for it.
        Assert.Equal([1, 2], wednesday.Where(stop => stop.AlternateSet == 1).Select(stop => stop.Sequence).Order());
    }

    /// <summary>
    /// A stop is unique within its route, day, week and set. Three Wednesdays of "CBD town" on the
    /// CBD/CZA truck are three different days and must survive; two of anything on one day is a slip.
    /// </summary>
    [Fact]
    public void SeedList_HasNoStopTwiceInTheSameSet()
    {
        // Across every route at once, because the seed key is unique database-wide — it carries the
        // route code. A collision between two routes would fail the unique index at start-up, which
        // is a failed deploy rather than a wrong number.
        var keys = VanSalesRouteSeedData.Routes
            .SelectMany(route => route.Stops.Select(stop => VanSalesRouteSeedData.SeedKeyOf(route.Code, stop)))
            .ToList();

        Assert.Equal(84, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

        // And the repeat that is legitimate is still there, on three separate days.
        var cbd = VanSalesRouteSeedData.Routes.Single(route => route.Code == "CBDCZA");

        Assert.Equal(3, cbd.Stops.Count(stop => stop.Name == "CBD town"));
    }

    // --- The seeder ---

    [Fact]
    public async Task Seeder_LoadsTheWholeScheduleIntoAnEmptyDatabase()
    {
        await SeedAsync();

        Assert.Equal(8, await _context.Routes.CountAsync());
        Assert.Equal(84, await _context.RouteStops.CountAsync());

        var east = await _context.Routes.SingleAsync(route => route.Code == "EAST");

        Assert.Equal("East Truck", east.Name);
        Assert.True(east.IsActive);

        var monday = await _context.RouteStops
            .Where(stop => stop.RouteId == east.Id && stop.DayOfWeek == DayOfWeek.Monday)
            .OrderBy(stop => stop.Sequence)
            .Select(stop => stop.Name)
            .ToListAsync();

        Assert.Equal(new[] { "Waterfalls", "Sunningdale", "Hatfield" }, monday);
    }

    /// <summary>
    /// Running it twice changes nothing. It runs on every application start, so a duplicate here is
    /// a duplicate per deploy.
    /// </summary>
    [Fact]
    public async Task Seeder_IsIdempotent()
    {
        await SeedAsync();
        await SeedAsync();
        await SeedAsync();

        Assert.Equal(8, await _context.Routes.CountAsync());
        Assert.Equal(84, await _context.RouteStops.CountAsync());
    }

    /// <summary>
    /// A route the office has renamed or retired keeps its edits. Rewriting them on start would undo
    /// the correction on the next deploy, and do it silently.
    /// </summary>
    [Fact]
    public async Task Seeder_DoesNotOverwriteARouteTheOfficeHasEdited()
    {
        await SeedAsync();

        var route = await _context.Routes.SingleAsync(r => r.Code == "WEST1");
        route.Name = "West 1 Truck (Mufakose)";
        route.TruckRegNo = "AHF0218";
        route.Territory = "Harare West";
        route.IsActive = false;
        await _context.SaveChangesAsync();

        await SeedAsync();

        var after = await _context.Routes.SingleAsync(r => r.Code == "WEST1");

        Assert.Equal("West 1 Truck (Mufakose)", after.Name);
        Assert.Equal("AHF0218", after.TruckRegNo);
        Assert.Equal("Harare West", after.Territory);
        Assert.False(after.IsActive);
        Assert.Equal(8, await _context.Routes.CountAsync());
    }

    /// <summary>
    /// A route whose code the office corrected is not created a second time.
    /// </summary>
    /// <remarks>
    /// The route-level form of the same bug. The code is the stable identifier a report groups on,
    /// which is exactly why somebody eventually fixes a wrong one — and matching on it would then
    /// make the next start decide the route had gone missing.
    /// </remarks>
    [Fact]
    public async Task Seeder_DoesNotRecreateARouteWhoseCodeChanged()
    {
        await SeedAsync();

        var route = await _context.Routes.SingleAsync(r => r.Code == "WEST2");
        route.Code = "HRE-W2";
        await _context.SaveChangesAsync();

        await SeedAsync();

        Assert.Equal(8, await _context.Routes.CountAsync());
        Assert.Equal(84, await _context.RouteStops.CountAsync());
        Assert.False(await _context.Routes.AnyAsync(r => r.Code == "WEST2"));
    }

    /// <summary>
    /// A code already taken by a hand-made route does not fail the start-up.
    /// </summary>
    /// <remarks>
    /// The unique index on <c>Code</c> would otherwise throw inside <c>SaveChangesAsync</c>, and
    /// because seeding runs during initialisation that is not one bad row — it is the application
    /// failing to start. The seeded route yields the code and takes a suffixed one, which is visible
    /// on the page and reconcilable; a crash is neither.
    /// </remarks>
    [Fact]
    public async Task Seeder_YieldsACodeAlreadyTakenByAHandMadeRoute()
    {
        _context.Routes.Add(new RouteEntity { Code = "EAST", Name = "Someone else's East", IsActive = true });
        await _context.SaveChangesAsync();

        await SeedAsync();

        Assert.Equal(9, await _context.Routes.CountAsync());
        Assert.Equal(84, await _context.RouteStops.CountAsync());

        var seeded = await _context.Routes.SingleAsync(r => r.SeedKey == "EAST");

        Assert.Equal("EAST-SEED", seeded.Code);
        Assert.Equal("East Truck", seeded.Name);
        Assert.Equal(13, await _context.RouteStops.CountAsync(stop => stop.RouteId == seeded.Id));

        // And it settles: a second start adds nothing further.
        await SeedAsync();
        Assert.Equal(9, await _context.Routes.CountAsync());
    }

    /// <summary>
    /// A stop dropped from the plan stays dropped. Removing a stop is a decision, not a gap for the
    /// seeder to fill, and its row is kept precisely so the seeder can tell the two apart.
    /// </summary>
    [Fact]
    public async Task Seeder_DoesNotBringBackAStopTheOfficeDropped()
    {
        await SeedAsync();

        var dropped = await _context.RouteStops.FirstAsync(stop => stop.Name == "Epworth");
        dropped.IsActive = false;
        await _context.SaveChangesAsync();

        await SeedAsync();

        var epworth = await _context.RouteStops.Where(stop => stop.Name == "Epworth").ToListAsync();

        Assert.Single(epworth);
        Assert.False(epworth[0].IsActive);
        Assert.Equal(84, await _context.RouteStops.CountAsync());
    }

    /// <summary>
    /// A stop added to the seed list later arrives on the next start, without a migration. That is
    /// the whole reason the schedule is a list rather than rows frozen into a migration.
    /// </summary>
    [Fact]
    public async Task Seeder_FillsAGapWithoutTouchingTheRest()
    {
        await SeedAsync();

        var removed = await _context.RouteStops.FirstAsync(stop => stop.Name == "Nyamapanda");
        var routeId = removed.RouteId;
        _context.RouteStops.Remove(removed);
        await _context.SaveChangesAsync();

        // One fewer than the schedule places, which is the gap this is about.
        Assert.Equal(83, await _context.RouteStops.CountAsync());

        await SeedAsync();

        var restored = await _context.RouteStops.SingleAsync(stop => stop.Name == "Nyamapanda");

        Assert.Equal(routeId, restored.RouteId);
        Assert.Equal(2, restored.WeekNumber);
        Assert.Null(restored.DayOfWeek);
        Assert.Equal(84, await _context.RouteStops.CountAsync());
    }

    /// <summary>
    /// The upcountry stops survive a re-run. They are the ones a SQL-side duplicate check would miss,
    /// because their day and week comparison is <c>NULL = NULL</c> — the seeder would find no match
    /// and insert all 42 of them again on every start.
    /// </summary>
    [Fact]
    public async Task Seeder_DoesNotDuplicateStopsThatHaveNoWeekday()
    {
        await SeedAsync();
        await SeedAsync();

        var upcountry = await _context.RouteStops
            .Where(stop => stop.DayOfWeek == null)
            .CountAsync();

        Assert.Equal(42, upcountry);
        Assert.Equal(1, await _context.RouteStops.CountAsync(stop => stop.Name == "Chiredzi"));
    }

    /// <summary>
    /// A stop the office renamed does not come back under its old name on the next start.
    /// </summary>
    /// <remarks>
    /// The case that forced <see cref="RouteStopEntity.SeedKey"/> to exist. While the seeder
    /// recognised its own rows by their contents, an edit hid the row from it — it looked for
    /// "Waterfalls", could not find it, and added it back, leaving Monday with both names. Renaming
    /// is the ordinary way a stop is corrected, so this fired on almost every deploy and did it
    /// silently: nobody reads a start-up log for an extra INSERT.
    /// </remarks>
    [Fact]
    public async Task Seeder_DoesNotResurrectARenamedStop()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");
        var stop = await _context.RouteStops.FirstAsync(s => s.Name == "Waterfalls");

        await SaveHandler().Handle(
            new SaveRouteStopCommand(
                stop.Id, route.Id, "Waterfalls Extension", DayOfWeek.Monday, null, 0, null, true, null),
            CancellationToken.None);
        _context.ChangeTracker.Clear();

        await SeedAsync();

        Assert.Equal(0, await _context.RouteStops.CountAsync(s => s.Name == "Waterfalls"));
        Assert.Equal(84, await _context.RouteStops.CountAsync());
    }

    /// <summary>
    /// A stop the office moved to another day does not come back on the old one.
    /// </summary>
    /// <remarks>
    /// The same failure as the rename above, reached by editing a different column. Both are listed
    /// because the fix has to be indifferent to <em>which</em> field changed, and a test of only one
    /// of them would pass against a fix that special-cased the name.
    /// </remarks>
    [Fact]
    public async Task Seeder_DoesNotResurrectARescheduledStop()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");
        var stop = await _context.RouteStops.FirstAsync(s => s.Name == "Epworth");

        await SaveHandler().Handle(
            new SaveRouteStopCommand(
                stop.Id, route.Id, "Epworth", DayOfWeek.Wednesday, null, 0, null, true, null),
            CancellationToken.None);
        _context.ChangeTracker.Clear();

        await SeedAsync();

        Assert.Equal(1, await _context.RouteStops.CountAsync(s => s.Name == "Epworth"));
        Assert.Equal(84, await _context.RouteStops.CountAsync());
    }

    /// <summary>
    /// A key the schedule no longer places is withdrawn on the next start.
    /// </summary>
    /// <remarks>
    /// Editing an entry's text in the seed list is a remove and add, not a rename — the key is
    /// derived from the name, so the edited entry arrives as a new stop while the row placed under
    /// the old name stays put. That is how "Domboshava Showgrounds", corrected from two stops back
    /// into one, would have become three on any database that had already run the earlier list.
    /// </remarks>
    [Fact]
    public async Task Seeder_WithdrawsAStopTheScheduleNoLongerPlaces()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "WEST2");

        // Stand in for a database seeded before the correction: the two halves of the old split,
        // carrying exactly the keys the retirement list names.
        _context.RouteStops.AddRange(
            new RouteStopEntity
            {
                RouteId = route.Id,
                Name = "Domboshava",
                DayOfWeek = DayOfWeek.Thursday,
                AlternateSet = 0,
                Sequence = 1,
                IsActive = true,
                SeedKey = "WEST2|4|-|0|DOMBOSHAVA"
            },
            new RouteStopEntity
            {
                RouteId = route.Id,
                Name = "Showgrounds",
                DayOfWeek = DayOfWeek.Thursday,
                AlternateSet = 0,
                Sequence = 2,
                IsActive = true,
                SeedKey = "WEST2|4|-|0|SHOWGROUNDS"
            });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await SeedAsync();

        var thursday = await _context.RouteStops
            .AsNoTracking()
            .Where(stop => stop.RouteId == route.Id && stop.DayOfWeek == DayOfWeek.Thursday
                && stop.IsActive)
            .Select(stop => stop.Name)
            .ToListAsync();

        Assert.Equal(new[] { "Domboshava Showgrounds" }, thursday);

        // Withdrawn, not deleted — the same treatment every other removal gets.
        Assert.Equal(2, await _context.RouteStops.CountAsync(stop =>
            stop.RouteId == route.Id && stop.DayOfWeek == DayOfWeek.Thursday && !stop.IsActive));
    }

    /// <summary>
    /// Withdrawing settles: a second start neither withdraws again nor re-places the stop.
    /// </summary>
    [Fact]
    public async Task Seeder_WithdrawalIsIdempotent()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "WEST2");

        _context.RouteStops.Add(new RouteStopEntity
        {
            RouteId = route.Id,
            Name = "Showgrounds",
            DayOfWeek = DayOfWeek.Thursday,
            AlternateSet = 0,
            Sequence = 9,
            IsActive = true,
            SeedKey = "WEST2|4|-|0|SHOWGROUNDS"
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await SeedAsync();
        await SeedAsync();

        Assert.Equal(85, await _context.RouteStops.CountAsync());
        Assert.Equal(84, await _context.RouteStops.CountAsync(stop => stop.IsActive));
    }

    /// <summary>
    /// Every retired key names a stop this list no longer places.
    /// </summary>
    /// <remarks>
    /// A retirement that still matches a live entry would deactivate the stop on every start and the
    /// page would show the round missing an area, with nothing on screen to say why. Cheap to state,
    /// and the retirement list is edited by hand.
    /// </remarks>
    [Fact]
    public void RetiredKeys_NameNothingTheScheduleStillPlaces()
    {
        var placed = VanSalesRouteSeedData.Routes
            .SelectMany(route => route.Stops.Select(stop => VanSalesRouteSeedData.SeedKeyOf(route.Code, stop)))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(
            VanSalesRouteSeedData.RetiredSeedKeys,
            key => Assert.DoesNotContain(key, placed));

        Assert.Equal(
            VanSalesRouteSeedData.RetiredSeedKeys.Count,
            VanSalesRouteSeedData.RetiredSeedKeys.Distinct(StringComparer.Ordinal).Count());
    }

    // --- Editing the plan afterwards ---

    [Fact]
    public async Task Save_AddsAStopToTheEndOfItsDay()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");

        var result = await SaveHandler().Handle(
            new SaveRouteStopCommand(
                Id: null,
                route.Id,
                "  Msasa  ",
                DayOfWeek.Monday,
                WeekNumber: null,
                AlternateSet: 0,
                Sequence: null,
                IsActive: true,
                ActingUserId: null),
            CancellationToken.None);

        Assert.False(result.IsError);

        // Trimmed, and appended after Waterfalls, Sunningdale and Hatfield rather than sharing a
        // position with the first of them.
        Assert.Equal("Msasa", result.Value.Name);
        Assert.Equal(4, result.Value.Sequence);
    }

    [Fact]
    public async Task Save_RefusesTheSameAreaTwiceOnTheSameDay()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");

        var result = await SaveHandler().Handle(
            new SaveRouteStopCommand(
                Id: null, route.Id, "waterfalls", DayOfWeek.Monday, null, 0, null, true, null),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Conflict, result.FirstError.Type);
    }

    /// <summary>
    /// The same refusal for an upcountry stop, whose day and week are null.
    /// </summary>
    /// <remarks>
    /// This is the case a duplicate check written as a <c>Where</c> clause lets straight through:
    /// <c>stop.DayOfWeek == command.DayOfWeek</c> translates to <c>NULL = NULL</c>, which SQL says is
    /// unknown, so no row matches and the duplicate is accepted. It reads as a working check until
    /// somebody adds Chiredzi to Upc 1 a second time.
    /// </remarks>
    [Fact]
    public async Task Save_RefusesADuplicateEvenWhenTheDayAndWeekAreNull()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "UPC1");

        var result = await SaveHandler().Handle(
            new SaveRouteStopCommand(
                Id: null, route.Id, "Chiredzi", DayOfWeek: null, WeekNumber: 1, 0, null, true, null),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Conflict, result.FirstError.Type);
    }

    /// <summary>
    /// The refusal names the stop as the schedule spells it, not as it was just typed.
    /// </summary>
    /// <remarks>
    /// The match is case-insensitive, so echoing the input would answer "already works waterfalls"
    /// about a stop the page shows as "Waterfalls" — which reads as a different stop and sends the
    /// reader looking for one that is not there.
    /// </remarks>
    [Fact]
    public async Task Save_NamesTheExistingStopInTheRefusal()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");

        var result = await SaveHandler().Handle(
            new SaveRouteStopCommand(null, route.Id, "waterfalls", DayOfWeek.Monday, null, 0, null, true, null),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Waterfalls", result.FirstError.Description);
        Assert.Contains("on Monday", result.FirstError.Description);
    }

    /// <summary>
    /// Adding a dropped stop back revives its row rather than refusing or creating a second one.
    /// </summary>
    /// <remarks>
    /// Refusing would be a dead end: the panel lists only active stops, so the reader would be told
    /// to edit one they cannot see. Creating a second row would leave two records of the same stop,
    /// one of them dropped, and the next drop would appear not to work.
    /// </remarks>
    [Fact]
    public async Task Save_RevivesAStopThatWasDropped()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");
        var dropped = await _context.RouteStops.FirstAsync(stop => stop.Name == "Epworth");
        var droppedId = dropped.Id;

        await DeleteHandler().Handle(new DeleteRouteStopCommand(droppedId, null), CancellationToken.None);
        _context.ChangeTracker.Clear();

        var result = await SaveHandler().Handle(
            new SaveRouteStopCommand(null, route.Id, "Epworth", DayOfWeek.Tuesday, null, 0, null, true, null),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(droppedId, result.Value.Id);
        Assert.True(result.Value.IsActive);
        Assert.Equal(84, await _context.RouteStops.CountAsync());
    }

    /// <summary>
    /// The same area on a different day, week or alternative set is a different stop. Three CBD town
    /// Wednesdays-to-Fridays exist for exactly this reason.
    /// </summary>
    [Fact]
    public async Task Save_AllowsTheSameAreaOnADifferentDayOrWeek()
    {
        await SeedAsync();

        var east = await _context.Routes.SingleAsync(r => r.Code == "EAST");
        var otherDay = await SaveHandler().Handle(
            new SaveRouteStopCommand(null, east.Id, "Waterfalls", DayOfWeek.Thursday, null, 0, null, true, null),
            CancellationToken.None);

        Assert.False(otherDay.IsError);

        var upc1 = await _context.Routes.SingleAsync(r => r.Code == "UPC1");
        var otherWeek = await SaveHandler().Handle(
            new SaveRouteStopCommand(null, upc1.Id, "Chiredzi", null, WeekNumber: 2, 0, null, true, null),
            CancellationToken.None);

        Assert.False(otherWeek.IsError);
    }

    [Fact]
    public async Task Save_RefusesAnUnnamedAreaAndAZerothWeek()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");

        var unnamed = await SaveHandler().Handle(
            new SaveRouteStopCommand(null, route.Id, "   ", DayOfWeek.Monday, null, 0, null, true, null),
            CancellationToken.None);

        Assert.True(unnamed.IsError);
        Assert.Equal(ErrorType.Validation, unnamed.FirstError.Type);

        var zerothWeek = await SaveHandler().Handle(
            new SaveRouteStopCommand(null, route.Id, "Msasa", null, WeekNumber: 0, 0, null, true, null),
            CancellationToken.None);

        Assert.True(zerothWeek.IsError);
        Assert.Equal(ErrorType.Validation, zerothWeek.FirstError.Type);
    }

    [Fact]
    public async Task Save_RefusesAStopOnARouteThatDoesNotExist()
    {
        await SeedAsync();

        var result = await SaveHandler().Handle(
            new SaveRouteStopCommand(null, 9999, "Msasa", DayOfWeek.Monday, null, 0, null, true, null),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.NotFound, result.FirstError.Type);
    }

    /// <summary>
    /// Dropping a stop deactivates it and keeps the row, so that "no longer called on" and "never
    /// called on" stay different histories — and so the seeder does not put it back.
    /// </summary>
    [Fact]
    public async Task Delete_DeactivatesRatherThanRemoving()
    {
        await SeedAsync();
        var stop = await _context.RouteStops.FirstAsync(s => s.Name == "Glenview");

        var result = await DeleteHandler().Handle(
            new DeleteRouteStopCommand(stop.Id, null),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(84, await _context.RouteStops.CountAsync());
        Assert.False(await _context.RouteStops.Where(s => s.Id == stop.Id).Select(s => s.IsActive).SingleAsync());
    }

    /// <summary>A second drop is success, not a conflict — the caller asked for a state.</summary>
    [Fact]
    public async Task Delete_IsSafeToRepeat()
    {
        await SeedAsync();
        var stop = await _context.RouteStops.FirstAsync(s => s.Name == "Glenview");

        await DeleteHandler().Handle(new DeleteRouteStopCommand(stop.Id, null), CancellationToken.None);
        var again = await DeleteHandler().Handle(new DeleteRouteStopCommand(stop.Id, null), CancellationToken.None);

        Assert.False(again.IsError);
    }

    // --- Reordering the round ---

    [Fact]
    public async Task Reorder_PutsAWeekdaysStopsInTheGivenOrder()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");

        var monday = await MondayAsync(route.Id);
        Assert.Equal(new[] { "Waterfalls", "Sunningdale", "Hatfield" }, monday.Select(s => s.Name));

        var result = await ReorderHandler().Handle(
            new ReorderRouteStopsCommand(
                route.Id, DayOfWeek.Monday, null, 0,
                [monday[2].Id, monday[0].Id, monday[1].Id],
                null),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(new[] { "Hatfield", "Waterfalls", "Sunningdale" }, result.Value.Select(s => s.Name));

        // Renumbered from 1 rather than left holding whatever they had, so the position a reader
        // sees on the page is the position stored.
        Assert.Equal([1, 2, 3], result.Value.Select(stop => stop.Sequence));

        var reread = await MondayAsync(route.Id);
        Assert.Equal(new[] { "Hatfield", "Waterfalls", "Sunningdale" }, reread.Select(s => s.Name));
    }

    /// <summary>
    /// An upcountry heading reorders too — the case a SQL-side filter silently drops.
    /// </summary>
    /// <remarks>
    /// Its day and week comparison is <c>NULL = NULL</c>, which SQL calls unknown, so a heading
    /// matched in a <c>Where</c> clause comes back empty and every reorder of an upcountry route is
    /// refused as "no stops there". It reads like a missing route rather than like a bug.
    /// </remarks>
    [Fact]
    public async Task Reorder_WorksOnAHeadingThatHasNoWeekday()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "UPC1");

        var week1 = await _context.RouteStops
            .AsNoTracking()
            .Where(stop => stop.RouteId == route.Id && stop.WeekNumber == 1 && stop.IsActive)
            .OrderBy(stop => stop.Sequence)
            .ToListAsync();

        var reversed = week1.AsEnumerable().Reverse().Select(stop => stop.Id).ToList();

        var result = await ReorderHandler().Handle(
            new ReorderRouteStopsCommand(route.Id, null, 1, 0, reversed, null),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            new[] { "Masvingo", "Chiredzi", "Nyika", "Gutu" },
            result.Value.Select(stop => stop.Name));
    }

    /// <summary>
    /// A weekday alternative reorders on its own, without disturbing the standard plan beside it.
    /// </summary>
    [Fact]
    public async Task Reorder_TreatsAnAlternativeAsItsOwnList()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "WEST2");

        var alternative = await _context.RouteStops
            .AsNoTracking()
            .Where(stop => stop.RouteId == route.Id && stop.DayOfWeek == DayOfWeek.Wednesday
                && stop.AlternateSet == 1)
            .OrderBy(stop => stop.Sequence)
            .ToListAsync();

        var result = await ReorderHandler().Handle(
            new ReorderRouteStopsCommand(
                route.Id, DayOfWeek.Wednesday, null, 1,
                [alternative[1].Id, alternative[0].Id],
                null),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(new[] { "Mungate", "Hatcliff" }, result.Value.Select(stop => stop.Name));

        var standard = await _context.RouteStops
            .AsNoTracking()
            .Where(stop => stop.RouteId == route.Id && stop.DayOfWeek == DayOfWeek.Wednesday
                && stop.AlternateSet == 0)
            .OrderBy(stop => stop.Sequence)
            .Select(stop => stop.Name)
            .ToListAsync();

        Assert.Equal(new[] { "Dzivarasekwa", "Whitehouse" }, standard);
    }

    /// <summary>
    /// An order that does not name every stop of its heading is refused, not applied.
    /// </summary>
    /// <remarks>
    /// The stale-page case, and the reason the whole list is sent rather than one stop's new
    /// position. Applied as a partial order, the stop nobody mentioned keeps whatever number it had —
    /// landing in the middle of the new numbering — and the page then shows an order no one chose.
    /// </remarks>
    [Fact]
    public async Task Reorder_RefusesAnOrderMissingAStop()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");
        var monday = await MondayAsync(route.Id);

        var result = await ReorderHandler().Handle(
            new ReorderRouteStopsCommand(
                route.Id, DayOfWeek.Monday, null, 0, [monday[1].Id, monday[0].Id], null),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Conflict, result.FirstError.Type);

        var unchanged = await MondayAsync(route.Id);
        Assert.Equal(new[] { "Waterfalls", "Sunningdale", "Hatfield" }, unchanged.Select(s => s.Name));
    }

    /// <summary>An order naming a stop from another heading is refused the same way.</summary>
    [Fact]
    public async Task Reorder_RefusesAnOrderNamingAStopFromElsewhere()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");
        var monday = await MondayAsync(route.Id);
        var epworth = await _context.RouteStops.AsNoTracking().FirstAsync(stop => stop.Name == "Epworth");

        var result = await ReorderHandler().Handle(
            new ReorderRouteStopsCommand(
                route.Id, DayOfWeek.Monday, null, 0,
                [monday[0].Id, monday[1].Id, monday[2].Id, epworth.Id],
                null),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(ErrorType.Conflict, result.FirstError.Type);

        _context.ChangeTracker.Clear();
        var stillTuesday = await _context.RouteStops.AsNoTracking().SingleAsync(s => s.Id == epworth.Id);
        Assert.Equal(DayOfWeek.Tuesday, stillTuesday.DayOfWeek);
    }

    /// <summary>
    /// A dropped stop is not part of its heading's order, and does not have to be named.
    /// </summary>
    [Fact]
    public async Task Reorder_IgnoresStopsDroppedFromThePlan()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");
        var monday = await MondayAsync(route.Id);

        await DeleteHandler().Handle(new DeleteRouteStopCommand(monday[1].Id, null), CancellationToken.None);
        _context.ChangeTracker.Clear();

        var result = await ReorderHandler().Handle(
            new ReorderRouteStopsCommand(
                route.Id, DayOfWeek.Monday, null, 0, [monday[2].Id, monday[0].Id], null),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(new[] { "Hatfield", "Waterfalls" }, result.Value.Select(stop => stop.Name));
    }

    [Fact]
    public async Task Reorder_RefusesARepeatedStopAndAnEmptyOrder()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");
        var monday = await MondayAsync(route.Id);

        var repeated = await ReorderHandler().Handle(
            new ReorderRouteStopsCommand(
                route.Id, DayOfWeek.Monday, null, 0,
                [monday[0].Id, monday[0].Id, monday[1].Id],
                null),
            CancellationToken.None);

        Assert.True(repeated.IsError);
        Assert.Equal(ErrorType.Validation, repeated.FirstError.Type);

        var empty = await ReorderHandler().Handle(
            new ReorderRouteStopsCommand(route.Id, DayOfWeek.Monday, null, 0, [], null),
            CancellationToken.None);

        Assert.True(empty.IsError);
        Assert.Equal(ErrorType.Validation, empty.FirstError.Type);
    }

    /// <summary>
    /// Reordering leaves <see cref="RouteStopEntity.SeedKey"/> alone, so the seeder still recognises
    /// the row and does not add the stop back at its original position on the next start.
    /// </summary>
    [Fact]
    public async Task Reorder_SurvivesTheNextStart()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "EAST");
        var monday = await MondayAsync(route.Id);

        await ReorderHandler().Handle(
            new ReorderRouteStopsCommand(
                route.Id, DayOfWeek.Monday, null, 0,
                [monday[2].Id, monday[1].Id, monday[0].Id],
                null),
            CancellationToken.None);

        await SeedAsync();

        var reread = await MondayAsync(route.Id);

        Assert.Equal(new[] { "Hatfield", "Sunningdale", "Waterfalls" }, reread.Select(s => s.Name));
        Assert.Equal(84, await _context.RouteStops.CountAsync());
    }

    private async Task<List<RouteStopEntity>> MondayAsync(int routeId)
    {
        _context.ChangeTracker.Clear();

        return await _context.RouteStops
            .AsNoTracking()
            .Where(stop => stop.RouteId == routeId && stop.DayOfWeek == DayOfWeek.Monday && stop.IsActive)
            .OrderBy(stop => stop.Sequence)
            .ToListAsync();
    }

    private ReorderRouteStopsHandler ReorderHandler()
        => new(_context, AuditSink(), NullLogger<ReorderRouteStopsHandler>.Instance);

    // --- Reading the plan ---

    [Fact]
    public async Task Query_HidesDroppedStopsUnlessAsked()
    {
        await SeedAsync();
        var stop = await _context.RouteStops.FirstAsync(s => s.Name == "Norton");
        stop.IsActive = false;
        await _context.SaveChangesAsync();

        var visible = await QueryHandler().Handle(new GetRouteStopsQuery(), CancellationToken.None);
        var all = await QueryHandler().Handle(new GetRouteStopsQuery(IncludeInactive: true), CancellationToken.None);

        Assert.Equal(83, visible.Value.Count);
        Assert.Equal(84, all.Value.Count);
        Assert.DoesNotContain(visible.Value, dto => dto.Name == "Norton");
    }

    /// <summary>
    /// One route's stops come back in the order its schedule prints them, labelled with the route
    /// they belong to.
    /// </summary>
    [Fact]
    public async Task Query_ReturnsOneRoutesStopsInScheduleOrder()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "WEST2");

        var result = await QueryHandler().Handle(
            new GetRouteStopsQuery(route.Id),
            CancellationToken.None);

        Assert.All(result.Value, dto => Assert.Equal("WEST2", dto.RouteCode));

        Assert.Equal(
            new[]
            {
                "Westlea", "Warren Park",
                "Kuwadzana",
                "Dzivarasekwa", "Whitehouse",
                "Hatcliff", "Mungate",
                "Domboshava Showgrounds",
                "Norton"
            },
            result.Value.Select(dto => dto.Name));
    }

    /// <summary>
    /// The upcountry weeks sort in cycle order, week 1 before week 2, with no weekday to sort on.
    /// </summary>
    [Fact]
    public async Task Query_OrdersUpcountryStopsByCycleWeek()
    {
        await SeedAsync();
        var route = await _context.Routes.SingleAsync(r => r.Code == "UPC4");

        var result = await QueryHandler().Handle(new GetRouteStopsQuery(route.Id), CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "Mapinga", "Chinhoyi", "Karoi", "Magunje", "Kariba",
                "Macheke", "Headlands", "Nyanga", "Watsomba", "Hauna", "Mutare"
            },
            result.Value.Select(dto => dto.Name));

        Assert.All(result.Value.Take(5), dto => Assert.Equal(1, dto.WeekNumber));
        Assert.All(result.Value.Skip(5), dto => Assert.Equal(2, dto.WeekNumber));
    }

    // --- Helpers ---

    private Task SeedAsync()
    {
        _context.ChangeTracker.Clear();
        return DbInitializer.SeedVanSalesRoutesAsync(_context, NullLogger.Instance);
    }

    private SaveRouteStopHandler SaveHandler()
        => new(_context, AuditSink(), NullLogger<SaveRouteStopHandler>.Instance);

    private DeleteRouteStopHandler DeleteHandler()
        => new(_context, AuditSink(), NullLogger<DeleteRouteStopHandler>.Instance);

    private GetRouteStopsHandler QueryHandler() => new(_context);

    private static IAuditService AuditSink()
        => StubProxy.For<IAuditService>((_, _) => Task.CompletedTask);
}
