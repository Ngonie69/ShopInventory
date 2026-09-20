using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.StockWriteOffs.Queries.GetStockWriteOffs;

public sealed class GetStockWriteOffsHandler(ApplicationDbContext context)
    : IRequestHandler<GetStockWriteOffsQuery, ErrorOr<StockWriteOffListResponseDto>>
{
    public async Task<ErrorOr<StockWriteOffListResponseDto>> Handle(
        GetStockWriteOffsQuery query,
        CancellationToken cancellationToken)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var rows = context.StockWriteOffs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim();
            rows = rows.Where(row => row.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.WarehouseCode))
        {
            var warehouse = query.WarehouseCode.Trim();
            rows = rows.Where(row => row.WarehouseCode == warehouse);
        }

        var totalCount = await rows.CountAsync(cancellationToken);

        var items = await rows
            .OrderByDescending(row => row.CreatedAtUtc)
            .ThenByDescending(row => row.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(StockWriteOffProjections.Summary)
            .ToListAsync(cancellationToken);

        // Counts are taken before the filters, so the status tabs keep their totals while one is open.
        var statusCounts = await context.StockWriteOffs
            .AsNoTracking()
            .GroupBy(row => row.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return new StockWriteOffListResponseDto
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            StatusCounts = statusCounts.ToDictionary(row => row.Status, row => row.Count)
        };
    }
}
