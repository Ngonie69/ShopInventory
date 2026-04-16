using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserActivity.Queries.GetMyActivity;

public sealed record GetMyActivityQuery(
    Guid? UserId,
    int RecentCount
) : IRequest<ErrorOr<UserActivitySummary>>;
