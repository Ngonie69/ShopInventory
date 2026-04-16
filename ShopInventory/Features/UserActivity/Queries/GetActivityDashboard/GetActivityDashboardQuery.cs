using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserActivity.Queries.GetActivityDashboard;

public sealed record GetActivityDashboardQuery(
    DateTime? StartDate,
    DateTime? EndDate
) : IRequest<ErrorOr<UserActivityDashboard>>;
