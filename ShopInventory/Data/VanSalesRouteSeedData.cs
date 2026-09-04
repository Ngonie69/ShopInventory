using ShopInventory.Models.Entities;

namespace ShopInventory.Data;

/// <summary>
/// The published van sales schedule: four upcountry routes running a two-week cycle, and four town
/// trucks running a weekday round.
/// </summary>
/// <remarks>
/// The single source of truth for the plan. <see cref="DbInitializer"/> applies it insert-only — a
/// route or a stop an administrator has since edited through the app is never overwritten from here,
/// and a stop added to this list later arrives on the next start without a migration.
/// <para>
/// The declarations below are laid out to be read against the schedule they came from, which is
/// quoted verbatim above each route. Check them that way: every difference between the quote and the
/// declaration is either a comma or one of the notes marked <c>SPLIT</c>, and there should be nothing
/// else to find.
/// </para>
/// <para>
/// Truck registrations are deliberately absent. The schedule names no vehicle, and a route's
/// registration is the default a rep confirms at departure — inventing one would put a plausible
/// wrong number in front of them to accept.
/// </para>
/// </remarks>
public static class VanSalesRouteSeedData
{
    /// <summary>A planned stop, before it is attached to a route.</summary>
    public sealed record StopSeed(
        string Name,
        DayOfWeek? DayOfWeek,
        int? WeekNumber,
        int AlternateSet,
        int Sequence);

    /// <summary>A route and the areas it works.</summary>
    public sealed record RouteSeed(
        string Code,
        string Name,
        string? Territory,
        IReadOnlyList<StopSeed> Stops);

    /// <summary>
    /// The territory the four upcountry routes share. They are numbered rather than named because
    /// each covers two unrelated parts of the country on alternating weeks.
    /// </summary>
    public const string UpcountryTerritory = "UPC";

    /// <summary>
    /// The territory the four town trucks share. A grouping label rather than a fact from the
    /// schedule, which names no territory at all — the office can rename it on the routes page.
    /// </summary>
    public const string TownTerritory = "Harare";

    public static readonly IReadOnlyList<RouteSeed> Routes =
    [
        // Upc 1-Gutu, Nyika,Chiredzi Masvingo
        // Upc 1-Mvurwi,Guruve,Mt Darwin,Madziva
        //
        // SPLIT: "Chiredzi Masvingo" is written without a comma, and is read here as the two towns.
        // They are 120km apart in the same province and every other entry on the line is a single
        // town, so one stop named for both would belong to neither.
        Upcountry("UPC1", "Upc 1",
            week1: ["Gutu", "Nyika", "Chiredzi", "Masvingo"],
            week2: ["Mvurwi", "Guruve", "Mt Darwin", "Madziva"]),

        // Upc 2-Beatrice,Mvuma,Mashava,Zvishavane,Chivi,Mhandamabwe
        // Upc 2-Goromonzi,Marondera,Rusape,Murambidza,Hwedza
        Upcountry("UPC2", "Upc 2",
            week1: ["Beatrice", "Mvuma", "Mashava", "Zvishavane", "Chivi", "Mhandamabwe"],
            week2: ["Goromonzi", "Marondera", "Rusape", "Murambidza", "Hwedza"]),

        // Upc 3-Murombedzi,Turf,Ngezi,Chegutu,Kadoma,Kwekwe
        // Upc 3-Bindura,Shamva,Murehwa,Mutoko,Kotwa,Nyamapanda
        Upcountry("UPC3", "Upc 3",
            week1: ["Murombedzi", "Turf", "Ngezi", "Chegutu", "Kadoma", "Kwekwe"],
            week2: ["Bindura", "Shamva", "Murehwa", "Mutoko", "Kotwa", "Nyamapanda"]),

        // Upc 4-Mapinga,Chinhoyi,Karoi,Magunje,Kariba
        // Upc 4-Macheke,Headlands,Nyanga,Watsomba,Hauna,Mutare
        Upcountry("UPC4", "Upc 4",
            week1: ["Mapinga", "Chinhoyi", "Karoi", "Magunje", "Kariba"],
            week2: ["Macheke", "Headlands", "Nyanga", "Watsomba", "Hauna", "Mutare"]),

        // East Truck
        // Monday-Waterfalls,Sunningdale,Hatfield
        // Tuesday-Epworth
        // Wednesday-Boka,Round About,Southlea Park
        // Thursday-Ruwa,Damafalls
        // Friday-Mabvuku,Tafara,Manresa,Gazebo
        TownTruck("EAST", "East Truck",
            Day(DayOfWeek.Monday, "Waterfalls", "Sunningdale", "Hatfield"),
            Day(DayOfWeek.Tuesday, "Epworth"),
            Day(DayOfWeek.Wednesday, "Boka", "Round About", "Southlea Park"),
            Day(DayOfWeek.Thursday, "Ruwa", "Damafalls"),
            Day(DayOfWeek.Friday, "Mabvuku", "Tafara", "Manresa", "Gazebo")),

        // WEST 1 Truck
        // Monday-Budiriro
        // Tuesday-Rugare,Kambuzuma,Mufakose
        // Wednesday-Mbare,Southerton,Machipisa
        // Thursday-Glenorah
        // Friday Glenview
        TownTruck("WEST1", "West 1 Truck",
            Day(DayOfWeek.Monday, "Budiriro"),
            Day(DayOfWeek.Tuesday, "Rugare", "Kambuzuma", "Mufakose"),
            Day(DayOfWeek.Wednesday, "Mbare", "Southerton", "Machipisa"),
            Day(DayOfWeek.Thursday, "Glenorah"),
            Day(DayOfWeek.Friday, "Glenview")),

        // West 2 Truck
        // Monday-Westlea,Warren Park
        // Tuesday-Kuwadzana
        // Wednesday-Dzivarasekwa,Whitehouse
        // Or Hatcliff,Mungate
        // Thursday-Domboshava Showgrounds
        // Friday-Norton
        //
        // NOT a split: "Domboshava Showgrounds" is one place. It was read here as the two — the
        // Showgrounds being in Harare itself — and the business corrected that on 2026-09-04. The
        // reading is recorded rather than quietly dropped because the same shape recurs twice more
        // in this file, and the correction is the evidence that a two-word entry cannot be split on
        // geography alone.
        TownTruck("WEST2", "West 2 Truck",
            Day(DayOfWeek.Monday, "Westlea", "Warren Park"),
            Day(DayOfWeek.Tuesday, "Kuwadzana"),
            Day(DayOfWeek.Wednesday, "Dzivarasekwa", "Whitehouse"),
            Alternative(DayOfWeek.Wednesday, "Hatcliff", "Mungate"),
            Day(DayOfWeek.Thursday, "Domboshava Showgrounds"),
            Day(DayOfWeek.Friday, "Norton")),

        // CBD/CZA Truck
        // Monday-Chitungwiza Unit L,C,M,N,Ziko
        // Tuesday-Chitungwiza St Mary's Zengeza,Manyame
        // Wednesday-CBD town
        // Thursday-CBD town
        // Friday CBD town
        //
        // SPLIT: both Chitungwiza days carry the town as a prefix on the first entry and list the
        // suburbs after it. Monday's bare "C,M,N" are units of Chitungwiza like the "Unit L" they
        // follow, and are written out in full — a stop named "C" names nothing. Tuesday's
        // "St Mary's Zengeza" is likewise two suburbs, not one.
        TownTruck("CBDCZA", "CBD/CZA Truck",
            Day(DayOfWeek.Monday,
                "Chitungwiza Unit L", "Chitungwiza Unit C", "Chitungwiza Unit M",
                "Chitungwiza Unit N", "Chitungwiza Ziko"),
            Day(DayOfWeek.Tuesday, "Chitungwiza St Mary's", "Chitungwiza Zengeza", "Chitungwiza Manyame"),
            Day(DayOfWeek.Wednesday, "CBD town"),
            Day(DayOfWeek.Thursday, "CBD town"),
            Day(DayOfWeek.Friday, "CBD town")),
    ];

    /// <summary>
    /// Stops this list used to place and no longer does, named by the key they were placed under.
    /// </summary>
    /// <remarks>
    /// Changing an area's text here is a <em>remove and add</em>, not a rename: the seed key is
    /// derived from the name, so the edited entry arrives as a new stop and the row placed under the
    /// old name stays exactly where it was. Left to itself that turns one corrected stop into two
    /// wrong ones, on a database that has already run the previous list — which is every machine
    /// that has run this branch.
    /// <para>
    /// Retiring the old key deactivates that row on the next start. It is a deactivation for the
    /// reason every other removal here is one, and it is a list rather than an inference because
    /// "this key is gone from the schedule" and "somebody renamed the row" look identical from the
    /// database and mean opposite things.
    /// </para>
    /// <para>
    /// Add to this whenever you change or delete an entry above that has already shipped. Nothing
    /// enforces it, so the retirement and the edit have to be made together.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> RetiredSeedKeys =
    [
        // Split from "Domboshava Showgrounds", which is one place. Corrected 2026-09-04.
        "WEST2|4|-|0|DOMBOSHAVA",
        "WEST2|4|-|0|SHOWGROUNDS",
    ];

    /// <summary>The stops of one weekday, in the order the schedule lists them.</summary>
    private static (DayOfWeek Day, int AlternateSet, string[] Areas) Day(DayOfWeek day, params string[] areas)
        => (day, 0, areas);

    /// <summary>
    /// The stops the schedule offers instead of that weekday's standard set — the "Or" line.
    /// </summary>
    private static (DayOfWeek Day, int AlternateSet, string[] Areas) Alternative(
        DayOfWeek day,
        params string[] areas)
        => (day, 1, areas);

    private static RouteSeed TownTruck(
        string code,
        string name,
        params (DayOfWeek Day, int AlternateSet, string[] Areas)[] days)
    {
        var stops = new List<StopSeed>();

        foreach (var (day, alternateSet, areas) in days)
        {
            // Sequence restarts per set rather than running through the route, so that the
            // alternative reads as its own list of stops and not as a continuation of the standard
            // one it replaces.
            var sequence = 1;

            foreach (var area in areas)
            {
                stops.Add(new StopSeed(area, day, WeekNumber: null, alternateSet, sequence++));
            }
        }

        return new RouteSeed(code, name, TownTerritory, stops);
    }

    private static RouteSeed Upcountry(string code, string name, string[] week1, string[] week2)
    {
        var stops = new List<StopSeed>();

        foreach (var (week, areas) in new[] { (1, week1), (2, week2) })
        {
            var sequence = 1;

            foreach (var area in areas)
            {
                // No weekday. An upcountry run is away for several days and the schedule commits to
                // the week, not to which morning the van reaches Chiredzi; writing a day here would
                // be an invention that every coverage report would then take literally.
                stops.Add(new StopSeed(area, DayOfWeek: null, week, AlternateSet: 0, sequence++));
            }
        }

        return new RouteSeed(code, name, UpcountryTerritory, stops);
    }

    /// <summary>
    /// The stable identity of one seeded stop: what the schedule placed, not what the row now says.
    /// </summary>
    /// <remarks>
    /// Derived only from this list, so it is the same string on every run whatever has since happened
    /// to the row. That is the whole point — see <see cref="RouteStopEntity.SeedKey"/>. Deriving it
    /// from the row instead would hide any edited row from the seeder, which would then add the
    /// original back.
    /// <para>
    /// Keyed on the route's <em>seed</em> code rather than its database id, so that a database
    /// restored or rebuilt elsewhere reproduces the same keys and the seeder stays idempotent across
    /// environments. Upper-cased because the only field a person retypes is the name, and two rows
    /// differing in case alone are one stop to everybody who reads the page.
    /// </para>
    /// </remarks>
    public static string SeedKeyOf(string routeCode, StopSeed stop)
        => string.Join(
            '|',
            routeCode.Trim().ToUpperInvariant(),
            stop.DayOfWeek is { } day ? ((int)day).ToString() : "-",
            stop.WeekNumber is { } week ? week.ToString() : "-",
            stop.AlternateSet.ToString(),
            stop.Name.Trim().ToUpperInvariant());
}
