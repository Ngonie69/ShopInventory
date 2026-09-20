using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.StockWriteOffs.Queries.GetStockWriteOff;

public sealed class GetStockWriteOffHandler(ApplicationDbContext context)
    : IRequestHandler<GetStockWriteOffQuery, ErrorOr<StockWriteOffDetailDto>>
{
    public async Task<ErrorOr<StockWriteOffDetailDto>> Handle(
        GetStockWriteOffQuery query,
        CancellationToken cancellationToken)
    {
        var writeOff = await context.StockWriteOffs
            .AsNoTracking()
            .Where(row => row.Id == query.WriteOffId)
            .Select(StockWriteOffProjections.Detail)
            .FirstOrDefaultAsync(cancellationToken);

        return writeOff is null
            ? Errors.StockWriteOff.NotFound(query.WriteOffId)
            : writeOff;
    }
}
