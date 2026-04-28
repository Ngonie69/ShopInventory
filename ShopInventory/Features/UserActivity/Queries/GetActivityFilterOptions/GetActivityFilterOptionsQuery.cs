using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserActivity.Queries.GetActivityFilterOptions;

public sealed record GetActivityFilterOptionsQuery(
    DateTime? StartDate,
    DateTime? EndDate
) : IRequest<ErrorOr<UserActivityFilterOptionsDto>>;