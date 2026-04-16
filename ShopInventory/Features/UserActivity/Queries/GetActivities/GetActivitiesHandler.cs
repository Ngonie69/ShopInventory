using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserActivity.Queries.GetActivities;

public sealed class GetActivitiesHandler(
    IUserActivityService userActivityService,
    ILogger<GetActivitiesHandler> logger
) : IRequestHandler<GetActivitiesQuery, ErrorOr<PagedResult<UserActivityItem>>>
{
    public async Task<ErrorOr<PagedResult<UserActivityItem>>> Handle(
        GetActivitiesQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var activities = await userActivityService.GetActivitiesAsync(
                query.Page, query.PageSize, query.UserId, query.Action,
                query.EntityType, query.StartDate, query.EndDate, cancellationToken);
            return activities;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching activities");
            return Errors.UserActivity.FetchFailed(ex.Message);
        }
    }
}
