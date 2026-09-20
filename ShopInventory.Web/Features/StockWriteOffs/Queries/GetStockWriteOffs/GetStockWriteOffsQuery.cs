using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOffs;

public sealed record GetStockWriteOffsQuery(string? Status, string? WarehouseCode, int Page, int PageSize)
    : IRequest<ErrorOr<StockWriteOffListResponse>>;
