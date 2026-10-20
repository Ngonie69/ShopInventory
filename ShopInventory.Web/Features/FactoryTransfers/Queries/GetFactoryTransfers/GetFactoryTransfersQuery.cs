using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.FactoryTransfers.Queries.GetFactoryTransfers;

public sealed record GetFactoryTransfersQuery(string? View, string? Search, int Page, int PageSize) : IRequest<ErrorOr<FactoryTransferListResponseDto>>;
