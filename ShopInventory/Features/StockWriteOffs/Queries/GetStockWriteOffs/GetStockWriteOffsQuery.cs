using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.StockWriteOffs.Queries.GetStockWriteOffs;

/// <summary>
/// One page of write-offs, newest first.
/// </summary>
/// <param name="Status">One status, or null for every one.</param>
/// <param name="WarehouseCode">One warehouse, or null for every one.</param>
public sealed record GetStockWriteOffsQuery(
    string? Status = null,
    string? WarehouseCode = null,
    int Page = 1,
    int PageSize = 25) : IRequest<ErrorOr<StockWriteOffListResponseDto>>;
