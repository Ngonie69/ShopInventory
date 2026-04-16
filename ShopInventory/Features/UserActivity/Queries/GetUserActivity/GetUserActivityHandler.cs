using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserActivity.Queries.GetUserActivity;

public sealed class GetUserActivityHandler(
    IUserActivityService userActivityService,
    ILogger<GetUserActivityHandler> logger
) : IRequestHandler<GetUserActivityQuery, ErrorOr<UserActivitySummary>>
{
    public async Task<ErrorOr<UserActivitySummary>> Handle(
        GetUserActivityQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var summary = await userActivityService.GetUserActivitySummaryAsync(
                query.UserId, query.RecentCount, cancellationToken);
            return summary;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.UserActivity.FetchFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching user activity for {UserId}", query.UserId);
            return Errors.UserActivity.FetchFailed(ex.Message);
        }
    }
}
