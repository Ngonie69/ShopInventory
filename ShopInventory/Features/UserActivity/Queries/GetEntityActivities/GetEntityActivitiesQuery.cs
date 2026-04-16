using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserActivity.Queries.GetEntityActivities;

public sealed record GetEntityActivitiesQuery(
    string EntityType,
    string EntityId
) : IRequest<ErrorOr<List<UserActivityItem>>>;
