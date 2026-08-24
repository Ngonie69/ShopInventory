namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerOrders;

/// <summary>A page of a customer's order history.</summary>
/// <remarks>
/// <c>TotalCount</c> is sent so the app can say "12 orders" and know when it has reached the end,
/// rather than paging until it gets a short page — which on a flaky connection is
/// indistinguishable from a request that failed.
/// </remarks>
public sealed record VanSalesOrderListResult(
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<VanSalesOrderResult> Orders);
