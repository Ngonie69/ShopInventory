using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesSaleByVanOrder;

/// <summary>
/// Whether a van sale posted under <paramref name="VanOrder"/> landed, and the receipt it carries if so.
/// </summary>
/// <remarks>
/// Asked by a handset whose <c>POST order</c> lost its reply. Always answered for an active account —
/// a sale that does not exist, and one the caller may not see, are both <c>found: false</c>.
/// </remarks>
public sealed record GetVanSalesSaleByVanOrderQuery(Guid UserId, string VanOrder)
    : IRequest<ErrorOr<VanSalesSaleLookupResponse>>;
