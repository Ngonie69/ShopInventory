using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesOrders;

/// <summary>
/// When the van is next due at a shop, and whether there is still time to order for that call.
/// </summary>
/// <remarks>
/// Deliberately free of the database and of <c>DateTime.UtcNow</c>: every input is a parameter, so
/// the awkward cases — the cut-off passing mid-afternoon, a Sunday visit day, a shop with no
/// schedule at all — can be stated as tests rather than waited for.
/// <para>
/// All the reasoning happens in CAT. A trading day is a day as the customer and the rep would name
/// it, and Zimbabwe is two hours ahead of UTC, so "Tuesday" decided in UTC is wrong for the first
/// two hours of every day. Conversions go through <see cref="AuditService.ToCAT"/> and
/// <see cref="AuditService.FromCAT"/> rather than a hardcoded offset, per the repo's time rules.
/// </para>
/// </remarks>
public static class VanSalesVisitSchedule
{
    /// <summary>How far ahead to look before giving up on finding a visit.</summary>
    /// <remarks>
    /// Two weeks. One would do for any schedule that has a day in it, but a fortnight means a
    /// misconfigured customer produces "no visit scheduled" rather than a date derived from a
    /// pattern nobody set.
    /// </remarks>
    private const int SearchHorizonDays = 14;

    /// <summary>
    /// The next call this shop can still order for.
    /// </summary>
    /// <param name="nowUtc">Current instant.</param>
    /// <param name="visitDays">The weekdays the van is due. Empty means no schedule is configured.</param>
    /// <param name="cutOffHoursBeforeVisitDay">
    /// How long before midnight CAT on the visit day ordering closes. 8 means 16:00 the day before.
    /// </param>
    public static VanSalesVisitWindow NextOpenVisit(
        DateTime nowUtc,
        IReadOnlyCollection<DayOfWeek> visitDays,
        int cutOffHoursBeforeVisitDay)
    {
        ArgumentNullException.ThrowIfNull(visitDays);

        if (visitDays.Count == 0)
        {
            // No schedule configured. Ordering stays open rather than being refused: a customer must
            // not be locked out of the app because nobody has filled in their calling days yet. The
            // order simply carries no requested date and goes on the next available run.
            return new VanSalesVisitWindow(null, null, HasSchedule: false, IsOrderingOpen: true);
        }

        var nowCat = AuditService.ToCAT(nowUtc);
        var today = nowCat.Date;

        for (var offset = 0; offset <= SearchHorizonDays; offset++)
        {
            var candidate = today.AddDays(offset);

            if (!visitDays.Contains(candidate.DayOfWeek))
            {
                continue;
            }

            var closesAtUtc = CutOffUtc(candidate, cutOffHoursBeforeVisitDay);

            // Strictly before: at the cut-off instant ordering is shut. A boundary that admitted
            // one more order is a van already loaded.
            if (nowUtc < closesAtUtc)
            {
                return new VanSalesVisitWindow(candidate, closesAtUtc, HasSchedule: true, IsOrderingOpen: true);
            }
        }

        // A schedule exists but nothing in it is still open — only reachable if the horizon is
        // shorter than the gap between calls, which no weekly pattern produces.
        return new VanSalesVisitWindow(null, null, HasSchedule: true, IsOrderingOpen: false);
    }

    /// <summary>
    /// The instant ordering closes for <paramref name="visitDateCat"/>.
    /// </summary>
    /// <remarks>
    /// Measured back from midnight CAT at the <em>start</em> of the visit day, so the setting reads
    /// the way it is explained to a shopkeeper: eight hours means "order by four the afternoon
    /// before".
    /// </remarks>
    public static DateTime CutOffUtc(DateTime visitDateCat, int cutOffHoursBeforeVisitDay)
    {
        var startOfVisitDayUtc = AuditService.FromCAT(visitDateCat.Date);
        return startOfVisitDayUtc.AddHours(-cutOffHoursBeforeVisitDay);
    }

    /// <summary>
    /// Whether <paramref name="visitDateCat"/> is a day this shop is called on and still open to order.
    /// </summary>
    /// <remarks>
    /// Used when an order names its own delivery date — an offline order submitted days later must
    /// be checked against the date it asks for, not against whatever is next when it finally lands.
    /// </remarks>
    public static bool IsOpenForVisitDate(
        DateTime nowUtc,
        DateTime visitDateCat,
        IReadOnlyCollection<DayOfWeek> visitDays,
        int cutOffHoursBeforeVisitDay)
    {
        ArgumentNullException.ThrowIfNull(visitDays);

        if (visitDays.Count > 0 && !visitDays.Contains(visitDateCat.DayOfWeek))
        {
            return false;
        }

        return nowUtc < CutOffUtc(visitDateCat, cutOffHoursBeforeVisitDay);
    }
}

/// <summary>
/// The next call a shop can order for, and whether it is still open.
/// </summary>
/// <param name="NextVisitDate">
/// The CAT calendar date of the call, midnight-based. Null when there is no schedule, or none of it
/// is still open.
/// </param>
/// <param name="OrdersCloseAtUtc">When ordering for that date shuts.</param>
/// <param name="HasSchedule">
/// Whether calling days are configured at all. Distinguishes "we do not know when you are next
/// called on" from "you are not called on", which the app words differently.
/// </param>
/// <param name="IsOrderingOpen">Whether an order can be placed right now.</param>
public sealed record VanSalesVisitWindow(
    DateTime? NextVisitDate,
    DateTime? OrdersCloseAtUtc,
    bool HasSchedule,
    bool IsOrderingOpen);
