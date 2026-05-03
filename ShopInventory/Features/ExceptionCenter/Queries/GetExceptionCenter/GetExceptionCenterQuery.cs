using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;

public sealed record GetExceptionCenterQuery(int Limit = 100) : IRequest<ErrorOr<ExceptionCenterDashboardDto>>;