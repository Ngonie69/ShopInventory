using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.ExceptionCenter.Queries.GetExceptionCenter;

public sealed record GetExceptionCenterQuery(int Limit = 100) : IRequest<ErrorOr<ExceptionCenterDashboardModel>>;