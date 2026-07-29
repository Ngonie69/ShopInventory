using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomers;

public sealed record GetVanSalesCustomersQuery(
    Guid UserId
) : IRequest<ErrorOr<List<VanSalesShopDto>>>;