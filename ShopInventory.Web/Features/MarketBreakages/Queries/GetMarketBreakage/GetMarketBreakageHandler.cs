using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.MarketBreakages.Queries.GetMarketBreakage;

public sealed class GetMarketBreakageHandler(
    IMarketBreakageService breakageService,
    ILogger<GetMarketBreakageHandler> logger)
    : IRequestHandler<GetMarketBreakageQuery, ErrorOr<MarketBreakageDetailDto>>
{
    public async Task<ErrorOr<MarketBreakageDetailDto>> Handle(
        GetMarketBreakageQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, detail) = await breakageService.GetBreakageAsync(request.Id);
            return success && detail is not null ? detail : Errors.MarketBreakage.LoadFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading market breakage report {Id}", request.Id);
            return Errors.MarketBreakage.LoadFailed($"Breakage report {request.Id} could not be loaded.");
        }
    }
}
