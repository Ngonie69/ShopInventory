using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Queries.GetSapSalesItems;

public sealed record GetSapSalesItemsQuery() : IRequest<ErrorOr<List<SapSalesItemDto>>>;
