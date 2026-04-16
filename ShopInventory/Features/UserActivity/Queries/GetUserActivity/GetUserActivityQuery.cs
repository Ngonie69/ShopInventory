using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserActivity.Queries.GetUserActivity;

public sealed record GetUserActivityQuery(
    Guid UserId,
    int RecentCount
) : IRequest<ErrorOr<UserActivitySummary>>;
