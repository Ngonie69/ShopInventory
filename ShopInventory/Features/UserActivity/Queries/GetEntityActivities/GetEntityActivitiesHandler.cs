using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserActivity.Queries.GetEntityActivities;

public sealed class GetEntityActivitiesHandler(
    IUserActivityService userActivityService,
    ILogger<GetEntityActivitiesHandler> logger
) : IRequestHandler<GetEntityActivitiesQuery, ErrorOr<List<UserActivityItem>>>
{
    public async Task<ErrorOr<List<UserActivityItem>>> Handle(
        GetEntityActivitiesQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var activities = await userActivityService.GetEntityActivitiesAsync(
                query.EntityType, query.EntityId, cancellationToken);
            return activities;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching entity activities for {EntityType}/{EntityId}",
                query.EntityType, query.EntityId);
            return Errors.UserActivity.FetchFailed(ex.Message);
        }
    }
}
