using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetInvoicesRequiringReview;

public sealed record GetInvoicesRequiringReviewQuery(
    int Limit = 50
) : IRequest<ErrorOr<List<InvoiceQueueStatusDto>>>;
