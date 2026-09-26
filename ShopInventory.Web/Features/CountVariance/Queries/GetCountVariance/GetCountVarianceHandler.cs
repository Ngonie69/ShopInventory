using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CountVariance.Queries.GetCountVariance;

public sealed class GetCountVarianceHandler(
    ICountVarianceService countVarianceService,
    ILogger<GetCountVarianceHandler> logger)
    : IRequestHandler<GetCountVarianceQuery, ErrorOr<CountVarianceReport>>
{
    public async Task<ErrorOr<CountVarianceReport>> Handle(
        GetCountVarianceQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, report) =
                await countVarianceService.GetReportAsync(request.DocumentEntry, cancellationToken);

            return success && report is not null ? report : Errors.CountVariance.LoadFailed(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading the variance of inventory count {DocumentEntry}", request.DocumentEntry);
            return Errors.CountVariance.LoadFailed("The count variance could not be loaded.");
        }
    }
}
