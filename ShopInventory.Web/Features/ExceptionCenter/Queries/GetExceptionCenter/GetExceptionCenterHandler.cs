using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.ExceptionCenter.Queries.GetExceptionCenter;

public sealed class GetExceptionCenterHandler(
    IExceptionCenterService exceptionCenterService,
    ILogger<GetExceptionCenterHandler> logger
) : IRequestHandler<GetExceptionCenterQuery, ErrorOr<ExceptionCenterDashboardModel>>
{
    public async Task<ErrorOr<ExceptionCenterDashboardModel>> Handle(
        GetExceptionCenterQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var dashboard = await exceptionCenterService.GetDashboardAsync(request.Limit, cancellationToken);
            if (dashboard is null)
            {
                return Errors.ExceptionCenter.LoadFailed("Failed to load exception center dashboard.");
            }

            return dashboard;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading exception center dashboard in web CQRS handler");
            return Errors.ExceptionCenter.LoadFailed("Failed to load exception center dashboard.");
        }
    }
}