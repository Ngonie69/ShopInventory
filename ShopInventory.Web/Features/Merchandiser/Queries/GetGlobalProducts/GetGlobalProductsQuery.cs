using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Merchandiser.Queries.GetGlobalProducts;

public sealed record GetGlobalProductsQuery : IRequest<ErrorOr<MerchandiserProductListResponse>>;