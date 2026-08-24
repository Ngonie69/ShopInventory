namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesOrdersForRoute;

/// <summary>
/// One item totalled across every order on the run.
/// </summary>
/// <remarks>
/// The figure the depot actually loads to. Per-order lines say who wants what; this says how much
/// to put on the truck, and working it out by hand from a dozen orders is exactly the arithmetic
/// that goes wrong at five in the afternoon.
/// </remarks>
public sealed record VanSalesLoadLine(
    string ItemCode,
    string? ItemDescription,
    string? UnitOfMeasure,
    decimal QuantityOrdered,
    int OrderCount);

/// <summary>What a van has been asked to carry, and by whom.</summary>
public sealed record VanSalesRouteLoadResult(
    DateTime? VisitDate,
    string? RouteCode,
    int OrderCount,
    decimal DocTotal,
    IReadOnlyList<VanSalesLoadLine> LoadLines,
    IReadOnlyList<VanSalesOrderResult> Orders);
