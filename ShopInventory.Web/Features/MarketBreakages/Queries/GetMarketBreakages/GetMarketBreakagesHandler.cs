using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.MarketBreakages.Queries.GetMarketBreakages;

public sealed class GetMarketBreakagesHandler(
    IMarketBreakageService breakageService,
    ILogger<GetMarketBreakagesHandler> logger)
    : IRequestHandler<GetMarketBreakagesQuery, ErrorOr<MarketBreakageListResponseDto>>
{
    public async Task<ErrorOr<MarketBreakageListResponseDto>> Handle(
        GetMarketBreakagesQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, response) = await breakageService.GetBreakagesAsync(
                request.Status, request.Search, request.Page, request.PageSize);
            return success && response is not null ? response : Errors.MarketBreakage.LoadFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading market breakage reports");
            return Errors.MarketBreakage.LoadFailed("The breakage reports could not be loaded.");
        }
    }
}
