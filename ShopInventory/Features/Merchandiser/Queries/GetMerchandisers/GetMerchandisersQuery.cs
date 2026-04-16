using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Queries.GetMerchandisers;

public sealed record GetMerchandisersQuery() : IRequest<ErrorOr<List<MerchandiserSummaryDto>>>;
