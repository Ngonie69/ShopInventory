using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesFiscalLease;

/// <param name="PendingSales">
/// Signed receipts this handset is still carrying, as it reports them. Null when it did not say — an
/// older build that predates the nomination — which is recorded as unknown rather than as none.
///
/// It is asked for on this call because this is the one request every handset makes whenever it has
/// signal, and because the office cannot safely move offline signing to another van while this one is
/// above zero. The server has no other way to find out.
/// </param>
public sealed record GetVanSalesFiscalLeaseQuery(Guid UserId, int? PendingSales = null)
    : IRequest<ErrorOr<VanSalesFiscalLeaseDto>>;
