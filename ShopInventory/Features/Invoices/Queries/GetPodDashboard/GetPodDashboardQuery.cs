using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetPodDashboard;

public sealed record GetPodDashboardQuery(Guid UserId) : IRequest<ErrorOr<PodDashboardDto>>;
