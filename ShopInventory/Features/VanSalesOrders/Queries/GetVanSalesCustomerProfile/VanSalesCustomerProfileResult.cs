namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerProfile;

/// <summary>
/// The signed-in shop, and the delivery window it is ordering into.
/// </summary>
/// <remarks>
/// Three of these need reading together rather than one at a time.
/// <list type="bullet">
/// <item><c>RouteCode</c>, <c>RouteName</c> and <c>Territory</c> are derived from the van serving
/// this shop and may be null. That is normal rather than exceptional — a van account without a
/// route recorded still trades — so the app shows the rest of the profile and omits the route
/// instead of treating it as an error.</item>
/// <item><c>NextVisitDate</c> is a CAT calendar date, and is null when no calling days are
/// configured; the order then goes on the next available run. <c>HasSchedule</c> is what separates
/// "we do not know when you are next called on" from "you are not called on", which the app words
/// differently.</item>
/// <item><c>OrdersCloseAtUtc</c> is the deadline the app counts down to. It is sent rather than
/// computed on the handset so a device with a wrong clock cannot invent its own.</item>
/// </list>
/// </remarks>
public sealed record VanSalesCustomerProfileResult(
    int AccountId,
    string CustomerCode,
    string CustomerName,
    string? DisplayName,
    string? Phone,
    string? Address,
    string? RouteCode,
    string? RouteName,
    string? Territory,
    IReadOnlyList<DayOfWeek> VisitDays,
    DateTime? NextVisitDate,
    DateTime? OrdersCloseAtUtc,
    bool HasSchedule,
    bool IsOrderingOpen);
