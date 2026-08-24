namespace ShopInventory.Features.RouteCustomers;

/// <summary>A shop's calling pattern, as the operator screen shows it.</summary>
public sealed record RouteCustomerVisitDaysResult(
    int RouteCustomerId,
    string Code,
    string Name,
    IReadOnlyList<DayOfWeek> VisitDays);
