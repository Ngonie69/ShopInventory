using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserActivity.Queries.GetActivities;

public sealed record GetActivitiesQuery(
    int Page,
    int PageSize,
    Guid? UserId,
    string? Action,
    string? EntityType,
    DateTime? StartDate,
    DateTime? EndDate
) : IRequest<ErrorOr<PagedResult<UserActivityItem>>>;
