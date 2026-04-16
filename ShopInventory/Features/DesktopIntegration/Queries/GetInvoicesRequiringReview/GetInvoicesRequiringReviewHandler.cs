using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetInvoicesRequiringReview;

public sealed class GetInvoicesRequiringReviewHandler(
    IInvoiceQueueService queueService
) : IRequestHandler<GetInvoicesRequiringReviewQuery, ErrorOr<List<InvoiceQueueStatusDto>>>
{
    public async Task<ErrorOr<List<InvoiceQueueStatusDto>>> Handle(
        GetInvoicesRequiringReviewQuery query,
        CancellationToken cancellationToken)
    {
        var invoices = await queueService.GetInvoicesRequiringReviewAsync(query.Limit, cancellationToken);
        return invoices;
    }
}
