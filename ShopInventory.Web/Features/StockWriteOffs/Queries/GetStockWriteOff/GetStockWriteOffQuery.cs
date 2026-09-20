using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOff;

public sealed record GetStockWriteOffQuery(int WriteOffId) : IRequest<ErrorOr<StockWriteOffDetail>>;
