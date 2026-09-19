using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakage;

public sealed class GetMarketBreakageHandler(ApplicationDbContext context)
    : IRequestHandler<GetMarketBreakageQuery, ErrorOr<MarketBreakageDetailDto>>
{
    public async Task<ErrorOr<MarketBreakageDetailDto>> Handle(
        GetMarketBreakageQuery query,
        CancellationToken cancellationToken)
    {
        var detail = await context.MarketBreakages
            .AsNoTracking()
            .Where(breakage => breakage.Id == query.BreakageId)
            .Select(MarketBreakageProjections.Detail)
            .FirstOrDefaultAsync(cancellationToken);

        return detail is null ? Errors.MarketBreakage.NotFound(query.BreakageId) : detail;
    }
}
