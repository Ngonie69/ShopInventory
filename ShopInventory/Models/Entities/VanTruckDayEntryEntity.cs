using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// What the office knows about one truck's day that no handset, invoice or tracker can say.
/// </summary>
/// <remarks>
/// <para>
/// The hand-kept daily workbook held four things the system had nowhere to put. A monthly target,
/// which is the route's. And three that are facts about a truck on a day:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Fuel issued.</b> The office hands it out, on a schedule, and nothing else records the litres.
/// The tracker's fuel figures are a sensor's, and most of the fleet has no sensor.
/// </item>
/// <item>
/// <b>Account sales.</b> A few customers on a round buy on account. Those invoices are keyed straight
/// into SAP and never pass through the van app, so the day's van takings do not include them and
/// nothing here can work them out. Checked against production for the week of 1 October 2026: no
/// document of any kind matched the workbook's figures.
/// </item>
/// <item>
/// <b>The area worked.</b> A town truck's weekday plan is stamped onto the day when the rep opens
/// it. An upcountry trip keeps no weekday, a day with a published alternative has two plans, and a
/// day nobody opened has no record at all. The office says which, here, and what it says wins.
/// </item>
/// </list>
/// <para>
/// Keyed on the route and the trading day, not on a rep's day record: two reps share a truck, and the
/// office enters these whether or not either of them tapped Start Day.
/// </para>
/// </remarks>
[Index(nameof(RouteId), nameof(TradingDate), IsUnique = true)]
[Index(nameof(TradingDate))]
public class VanTruckDayEntryEntity
{
    [Key]
    public int Id { get; set; }

    public int RouteId { get; set; }

    [ForeignKey(nameof(RouteId))]
    public RouteEntity? Route { get; set; }

    /// <summary>The trading day in CAT, as a bare date. See <see cref="VanRouteDayEntity.TradingDate"/>.</summary>
    [Column(TypeName = "date")]
    public DateTime TradingDate { get; set; }

    /// <summary>The area the truck worked, as the office names it. Null leaves the day's own stamp to stand.</summary>
    [MaxLength(200)]
    public string? AreaName { get; set; }

    /// <summary>Litres of fuel issued to the truck that day. Null means none was entered, not none was issued.</summary>
    [Column(TypeName = "decimal(9,2)")]
    public decimal? FuelLitres { get; set; }

    /// <summary>Sales to account customers on the round, invoiced in SAP outside the van app, VAT included.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? AccountSales { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    [MaxLength(100)]
    public string? UpdatedByName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
