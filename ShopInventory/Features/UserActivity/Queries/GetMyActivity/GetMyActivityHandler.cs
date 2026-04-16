using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserActivity.Queries.GetMyActivity;

public sealed class GetMyActivityHandler(
    IUserActivityService userActivityService,
    ILogger<GetMyActivityHandler> logger
) : IRequestHandler<GetMyActivityQuery, ErrorOr<UserActivitySummary>>
{
    public async Task<ErrorOr<UserActivitySummary>> Handle(
        GetMyActivityQuery query,
        CancellationToken cancellationToken)
    {
        if (query.UserId is null)
        {
            return Errors.UserActivity.Unauthenticated;
        }

        try
        {
            var summary = await userActivityService.GetUserActivitySummaryAsync(
                query.UserId.Value, query.RecentCount, cancellationToken);
            return summary;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.UserActivity.FetchFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching activity for current user");
            return Errors.UserActivity.FetchFailed(ex.Message);
        }
    }
}
