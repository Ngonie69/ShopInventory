using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.ExceptionCenter.Queries.GetExceptionCenter;

public sealed record GetExceptionCenterQuery(int Limit = 150, string? Assignee = null)
    : IRequest<ErrorOr<ExceptionCenterDashboardModel>>;
