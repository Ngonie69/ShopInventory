using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesFiscal;

public sealed record GetVanSalesFiscalQuery(Guid UserId) : IRequest<ErrorOr<VanSalesLegacyFiscalDto>>;