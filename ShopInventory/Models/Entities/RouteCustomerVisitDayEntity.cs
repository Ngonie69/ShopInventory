using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A weekday on which the van is expected at a particular shop.
///
/// The round has always run to a pattern — this shop on Tuesdays, that one on Tuesdays and
/// Fridays — but the pattern lived in the rep's head and on paper. Nothing in the system could say
/// when a given customer would next be called on, which is the first question the customer asks and
/// the one an ordering app has to answer before it can ask for an order.
/// </summary>
/// <remarks>
/// A row per day rather than a bitmask on the customer, because the question asked of this data is
/// "who is due on Tuesday?" — a lookup against an index, not a scan of every customer testing a bit.
/// The load list for a van's morning is exactly that query.
/// <para>
/// This is the <em>plan</em>. What actually happened on a given day is
/// <see cref="VanRouteDayEntity"/> and the visits recorded against it, and the two are deliberately
/// separate: a van that skips a shop must not retroactively edit the schedule it was measured
/// against.
/// </para>
/// <para>
/// Note there is no route on this row. A route customer's van — and therefore its route — is its
/// <see cref="RouteCustomerEntity.AssignedBusinessPartnerCode"/>; copying the route here as well
/// would create a second answer that drifts the first time someone reassigns a shop.
/// </para>
/// </remarks>
[Index(nameof(RouteCustomerId), nameof(DayOfWeek), IsUnique = true)]
[Index(nameof(DayOfWeek))]
public class RouteCustomerVisitDayEntity
{
    [Key]
    public int Id { get; set; }

    public int RouteCustomerId { get; set; }

    [ForeignKey(nameof(RouteCustomerId))]
    public RouteCustomerEntity? RouteCustomer { get; set; }

    /// <summary>
    /// The weekday of the call, stored as <see cref="System.DayOfWeek"/>'s own numbering
    /// (Sunday = 0).
    /// </summary>
    /// <remarks>
    /// Kept as the framework's numbering rather than an ISO-8601 one so that no conversion sits
    /// between this column and <c>DateTime.DayOfWeek</c>. An off-by-one here would move every
    /// customer's delivery a day and read as a plausible schedule while doing it.
    /// </remarks>
    public DayOfWeek DayOfWeek { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
