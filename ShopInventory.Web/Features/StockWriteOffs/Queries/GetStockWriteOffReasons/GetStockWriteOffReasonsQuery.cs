using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOffReasons;

public sealed record GetStockWriteOffReasonsQuery : IRequest<ErrorOr<StockWriteOffReasonsResponse>>;
