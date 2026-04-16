using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserActivity.Queries.GetActivityDashboard;

public sealed class GetActivityDashboardHandler(
    IUserActivityService userActivityService,
    ILogger<GetActivityDashboardHandler> logger
) : IRequestHandler<GetActivityDashboardQuery, ErrorOr<UserActivityDashboard>>
{
    public async Task<ErrorOr<UserActivityDashboard>> Handle(
        GetActivityDashboardQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var dashboard = await userActivityService.GetDashboardAsync(
                query.StartDate, query.EndDate, cancellationToken);
            return dashboard;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching activity dashboard");
            return Errors.UserActivity.FetchFailed(ex.Message);
        }
    }
}
