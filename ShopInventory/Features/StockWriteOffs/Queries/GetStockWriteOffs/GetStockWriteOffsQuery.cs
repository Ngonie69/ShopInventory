using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.StockWriteOffs.Queries.GetStockWriteOffs;

/// <summary>
/// One page of write-offs, newest first.
/// </summary>
/// <param name="Status">One status, or null for every one.</param>
/// <param name="WarehouseCode">One warehouse, or null for every one.</param>
/// <param name="Page">The page to return, counting from 1; anything lower is read as 1.</param>
/// <param name="PageSize">How many write-offs a page holds, held between 1 and 200.</param>
public sealed record GetStockWriteOffsQuery(
    string? Status = null,
    string? WarehouseCode = null,
    int Page = 1,
    int PageSize = 25) : IRequest<ErrorOr<StockWriteOffListResponseDto>>;
