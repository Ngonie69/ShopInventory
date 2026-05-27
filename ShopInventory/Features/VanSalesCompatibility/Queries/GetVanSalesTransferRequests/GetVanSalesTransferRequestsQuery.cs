using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesTransferRequests;

public sealed record GetVanSalesTransferRequestsQuery(Guid UserId) : IRequest<ErrorOr<List<VanSalesLegacyInventoryOrderDto>>>;