using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesFiscalLease;

public sealed record GetVanSalesFiscalLeaseQuery(Guid UserId) : IRequest<ErrorOr<VanSalesFiscalLeaseDto>>;
