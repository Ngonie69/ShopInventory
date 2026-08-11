using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Mobile;

/// <summary>
/// Who a van sale bills, and who it was actually sold to. On a route-customer van those are two
/// different parties and both have to survive the write.
///
/// <see cref="PostingCardCode"/> is the SAP business partner the document posts against. For a van whose
/// customers are real business partners that is the customer itself; for a route-customer van it is the
/// van's own account, because the individual shops do not exist in SAP.
///
/// <see cref="RouteCustomer"/> is the shop, and it is set only in that second case. Recording it is what
/// makes a route's takings divisible by customer — the posting code alone cannot say who bought, because
/// every sale the van makes carries the same one.
/// </summary>
public sealed record VanSalesCustomerResolution(string PostingCardCode, RouteCustomerEntity? RouteCustomer)
{
    public int? RouteCustomerId => RouteCustomer?.Id;

    public string? RouteCustomerCode => RouteCustomer?.Code;

    public string? RouteCustomerName => RouteCustomer?.Name;
}
