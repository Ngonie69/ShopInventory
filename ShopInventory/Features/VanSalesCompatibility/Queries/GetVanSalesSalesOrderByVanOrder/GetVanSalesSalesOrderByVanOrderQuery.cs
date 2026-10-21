using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesSalesOrderByVanOrder;

/// <summary>
/// Whether a van sales order sent under <paramref name="VanOrder"/> landed.
/// </summary>
/// <remarks>
/// Asked by a handset whose <c>POST sales-order</c> lost its reply. Always answered for an active account —
/// an order that does not exist, and one the caller may not see, are both <c>found: false</c>.
/// </remarks>
public sealed record GetVanSalesSalesOrderByVanOrderQuery(Guid UserId, string VanOrder)
    : IRequest<ErrorOr<VanSalesSalesOrderLookupResponse>>;
