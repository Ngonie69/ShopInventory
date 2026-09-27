using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CountVariance.Queries.GetVanCountVariance;

public sealed class GetVanCountVarianceHandler(
    ICountVarianceService countVarianceService,
    ILogger<GetVanCountVarianceHandler> logger)
    : IRequestHandler<GetVanCountVarianceQuery, ErrorOr<VanCountVarianceReport>>
{
    public async Task<ErrorOr<VanCountVarianceReport>> Handle(
        GetVanCountVarianceQuery request,
        CancellationToken cancellationToken)
    {
        if (request.ToDate.Date < request.FromDate.Date)
            return Errors.CountVariance.LoadFailed("The range must end on or after the day it starts.");

        try
        {
            var (success, message, report) = await countVarianceService.GetVanReportAsync(
                request.FromDate.Date, request.ToDate.Date, cancellationToken);

            return success && report is not null ? report : Errors.CountVariance.LoadFailed(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error loading the van count variance for {From:yyyy-MM-dd} to {To:yyyy-MM-dd}",
                request.FromDate, request.ToDate);
            return Errors.CountVariance.LoadFailed("The van counts could not be loaded.");
        }
    }
}
