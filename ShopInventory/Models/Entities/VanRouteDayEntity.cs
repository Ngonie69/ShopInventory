using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// One rep's trading day on a route: out of the depot in the morning, back in the evening.
///
/// This is the departure compliance sheet made into data. Everything above the fold of that sheet —
/// territory, route, time out, time in, truck registration, opening and closing mileage, RTI, and the
/// cash the rep declares at the end — was captured by nothing in either the portal or the handset, so
/// the sheet was filled in by hand and the system could not check a line of it.
///
/// Only the facts a person has to supply live here. Everything the day can be *asked* — how many calls
/// were made, how many bought, what was sold and to whom — is derived from the visits and the sales
/// rather than typed twice, because a figure entered by the same person it measures is not a control.
/// The one exception is the declared cash, which is deliberately duplicated: the point of asking the
/// rep to count the takings is to compare their count against the system's.
/// </summary>
/// <remarks>
/// One row per rep per trading day. The unique index says so, and it is what makes "start my day"
/// safe to tap twice on a bad line — the second attempt collides rather than opening a second day.
/// </remarks>
[Index(nameof(UserId), nameof(TradingDate), IsUnique = true)]
[Index(nameof(TradingDate))]
[Index(nameof(RouteCode), nameof(TradingDate))]
[Index(nameof(StartClientReference), IsUnique = true)]
[Index(nameof(EndClientReference), IsUnique = true)]
public class VanRouteDayEntity
{
    [Key]
    public int Id { get; set; }

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }

    [Required]
    [MaxLength(50)]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The trading day in CAT, as a bare date. Deliberately not derived from
    /// <see cref="DepartedAt"/> at read time: a van that leaves at 05:30 and one that leaves at 19:00
    /// on a late run both belong to the day the rep would name, and only the handset knows which that
    /// is. It matches <c>DesktopSaleEntity.DocDate</c>, which the sales side keeps the same way.
    /// </summary>
    [Column(TypeName = "date")]
    public DateTime TradingDate { get; set; }

    // --- The route as it stood on the day ---
    //
    // Snapshots, not a convenience copy of the joined row, for the reason DesktopSaleEntity gives for
    // its own: routes are renamed and retired, and last month's compliance report must not change
    // because someone tidied the route list this morning.

    public int? RouteId { get; set; }

    [ForeignKey(nameof(RouteId))]
    public RouteEntity? Route { get; set; }

    [MaxLength(30)]
    public string? RouteCode { get; set; }

    [MaxLength(100)]
    public string? RouteName { get; set; }

    [MaxLength(100)]
    public string? Territory { get; set; }

    /// <summary>
    /// The truck actually driven, confirmed by the rep at departure. The route's own registration is
    /// only the default offered — a vehicle goes into the workshop and the round still runs.
    /// </summary>
    [MaxLength(30)]
    public string? TruckRegNo { get; set; }

    // --- Departure ---

    public DateTime DepartedAt { get; set; }

    public DateTime? DepartedRecordedAt { get; set; }

    public double? DepartedLatitude { get; set; }

    public double? DepartedLongitude { get; set; }

    /// <summary>Odometer on leaving the depot.</summary>
    public int? StartingMileage { get; set; }

    /// <summary>
    /// How many customers the route held when the day opened — the denominator of the call
    /// compliance rate.
    ///
    /// Snapshotted at departure rather than counted when the report runs, because route customers are
    /// hard-deleted and freely reassigned. Counting later means a shop removed in September silently
    /// improves every CCR back to January, which is precisely the number nobody would notice was
    /// wrong.
    /// </summary>
    public int PlannedCustomerCount { get; set; }

    // --- Return ---

    public DateTime? ReturnedAt { get; set; }

    public DateTime? ReturnedRecordedAt { get; set; }

    public double? ReturnedLatitude { get; set; }

    public double? ReturnedLongitude { get; set; }

    public int? ClosingMileage { get; set; }

    /// <summary>
    /// Kilometres travelled. Null until the day is closed, and never negative — an odometer that
    /// appears to have run backwards is a typo, and reporting a negative distance would quietly
    /// subtract from a fleet total.
    /// </summary>
    [NotMapped]
    public int? KilometresTravelled =>
        StartingMileage is { } start && ClosingMileage is { } close && close >= start
            ? close - start
            : null;

    // --- Returnable transit items ---

    /// <summary>Crates and pallets taken out.</summary>
    public int? RtiOut { get; set; }

    /// <summary>Crates and pallets brought back.</summary>
    public int? RtiReturned { get; set; }

    [NotMapped]
    public int? RtiOutstanding =>
        RtiOut is { } issued && RtiReturned is { } returned ? issued - returned : null;

    // --- What the rep counted ---
    //
    // The report shows these beside the system's own totals from the day's sales. Where they differ,
    // the difference is the finding — which is the whole reason for collecting both.

    [Column(TypeName = "decimal(18,2)")]
    public decimal? DeclaredCash { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? DeclaredEcocash { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? DeclaredInnbucks { get; set; }

    [MaxLength(10)]
    public string? DeclaredCurrency { get; set; }

    [NotMapped]
    public decimal? DeclaredTotal =>
        DeclaredCash is null && DeclaredEcocash is null && DeclaredInnbucks is null
            ? null
            : (DeclaredCash ?? 0) + (DeclaredEcocash ?? 0) + (DeclaredInnbucks ?? 0);

    [MaxLength(1000)]
    public string? Notes { get; set; }

    // --- Provenance ---

    [MaxLength(100)]
    public string? StartClientReference { get; set; }

    [MaxLength(100)]
    public string? EndClientReference { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    /// <summary>Whether the rep has closed the day. An open day is not a finished one to report on.</summary>
    [NotMapped]
    public bool IsClosed => ReturnedAt.HasValue;
}
