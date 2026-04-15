using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Queries.GetPodDashboard;

public sealed class GetPodDashboardHandler(
    IDocumentService documentService
) : IRequestHandler<GetPodDashboardQuery, ErrorOr<PodDashboardDto>>
{
    public async Task<ErrorOr<PodDashboardDto>> Handle(
        GetPodDashboardQuery request,
        CancellationToken cancellationToken)
    {
        var dashboard = await documentService.GetPodDashboardAsync(request.UserId, cancellationToken);
        return dashboard;
    }
}
