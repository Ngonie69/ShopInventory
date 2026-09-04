using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One area a route is expected to work, and when.
///
/// The rounds have always run to a published plan — East Truck does Waterfalls, Sunningdale and
/// Hatfield on a Monday; Upc 1 goes south to Gutu and Chiredzi one week and north to Guruve and
/// Mt Darwin the next — but the plan lived on a sheet of paper. <see cref="RouteEntity"/> could name
/// the route and the truck and nothing could say where either was supposed to be, so no report could
/// ask whether a van worked the areas it was sent to.
/// </summary>
/// <remarks>
/// An <em>area</em>, not a shop. <see cref="RouteCustomerEntity"/> already holds the shops a van
/// trades with and <see cref="RouteCustomerVisitDayEntity"/> the day each is called on; those are
/// keyed on the van's business partner and are built up a customer at a time by the rep. This is the
/// office's plan for the round itself, written in the names the schedule uses — "Epworth", "Kariba" —
/// which name no customer and never will. The two meet later, when a shop's address places it in an
/// area; keeping them apart means the plan does not have to wait for the customer master to be right.
/// <para>
/// The three shapes the published schedule uses all fit one row, and the columns are only what it
/// takes to tell them apart:
/// </para>
/// <list type="bullet">
/// <item>
/// A town truck works a weekday every week: <see cref="DayOfWeek"/> set, <see cref="WeekNumber"/>
/// null.
/// </item>
/// <item>
/// An upcountry route runs a two-week cycle with no fixed weekday — the whole trip is the unit:
/// <see cref="DayOfWeek"/> null, <see cref="WeekNumber"/> 1 or 2.
/// </item>
/// <item>
/// A day with a published alternative — West 2's Wednesday is Dzivarasekwa and Whitehouse
/// <em>or</em> Hatcliff and Mungate — puts the alternative in <see cref="AlternateSet"/> 1. Both sets
/// are the plan; which one ran is a fact about the day, and belongs on
/// <see cref="VanRouteDayEntity"/> rather than here.
/// </item>
/// </list>
/// <para>
/// There is deliberately no unique index over the (route, day, week, set, name) tuple. Two of those
/// columns are nullable and PostgreSQL counts distinct NULLs as distinct rows, so such an index would
/// pass silently while permitting exactly the duplicate it claims to prevent — worse than none,
/// because a reader would trust it. The save handler compares that tuple in memory instead.
/// </para>
/// </remarks>
[Index(nameof(RouteId), nameof(DayOfWeek))]
[Index(nameof(RouteId), nameof(IsActive))]
[Index(nameof(SeedKey), IsUnique = true)]
public class RouteStopEntity
{
    [Key]
    public int Id { get; set; }

    public int RouteId { get; set; }

    [ForeignKey(nameof(RouteId))]
    public RouteEntity? Route { get; set; }

    /// <summary>The area as the schedule names it: "Waterfalls", "Mt Darwin", "CBD town".</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// The weekday the area is worked, in <see cref="System.DayOfWeek"/>'s own numbering
    /// (Sunday = 0), or null when the route keeps no weekday — an upcountry trip is away for days at
    /// a time and its stops fall where the run reaches them.
    /// </summary>
    /// <remarks>
    /// The framework's numbering rather than an ISO-8601 one, for the reason
    /// <see cref="RouteCustomerVisitDayEntity.DayOfWeek"/> gives: an off-by-one here moves every stop
    /// a day and still reads as a plausible schedule.
    /// </remarks>
    public DayOfWeek? DayOfWeek { get; set; }

    /// <summary>
    /// Which week of the route's repeating cycle the stop belongs to, 1-based, or null when the stop
    /// is worked every week.
    /// </summary>
    /// <remarks>
    /// Null is not week 1. A town truck repeats weekly and has no cycle at all, and writing 1 there
    /// would make it indistinguishable from the first week of a fortnightly upcountry run.
    /// </remarks>
    public int? WeekNumber { get; set; }

    /// <summary>
    /// Which published set of stops this belongs to for its day: 0 is the standard plan, 1 and above
    /// are the alternatives the schedule offers instead of it.
    /// </summary>
    public int AlternateSet { get; set; }

    /// <summary>Order within its day or week, as the schedule lists them.</summary>
    public int Sequence { get; set; }

    /// <summary>
    /// Whether the stop is still in the plan. Dropped stops are deactivated rather than deleted, for
    /// the reason <c>DeleteRouteCustomerHandler</c> keeps its rows: "no longer called on" and "never
    /// called on" are different histories, and only one of them is a change worth seeing.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// What the published schedule placed here, or null on a stop somebody added themselves.
    /// </summary>
    /// <remarks>
    /// The seeder's memory, and the reason the office can edit a seeded stop at all. It records what
    /// the seed <em>put</em> on this row — never what the row now says — so it is written once, at
    /// insert, and no edit touches it.
    /// <para>
    /// Without it the seeder has to recognise its own rows by their contents, and then any edit to
    /// those contents hides the row from it: rename Waterfalls and the next start cannot find
    /// Waterfalls, so it helpfully adds it back, and Monday now has both. Renaming and rescheduling
    /// are the ordinary way this data is corrected, and it would have re-corrected itself on every
    /// deploy, quietly.
    /// </para>
    /// <para>
    /// Unique, and nullable so that hand-added stops — all of which are NULL — do not collide.
    /// PostgreSQL counts NULLs as distinct in a unique index, which is exactly right here and is the
    /// same behaviour that makes such an index useless over the scheduling tuple above.
    /// </para>
    /// </remarks>
    [MaxLength(120)]
    public string? SeedKey { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
