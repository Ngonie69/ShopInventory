using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;

/// <param name="Limit">Maximum work-queue items to return.</param>
/// <param name="Assignee">
/// Username the caller is viewing as, used only to fill the "assigned to me" count.
/// </param>
public sealed record GetExceptionCenterQuery(int Limit = 100, string? Assignee = null)
    : IRequest<ErrorOr<ExceptionCenterDashboardDto>>;
