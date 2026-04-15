using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Queries.GetInvoiceAttachments;

public sealed class GetInvoiceAttachmentsHandler(
    IDocumentService documentService
) : IRequestHandler<GetInvoiceAttachmentsQuery, ErrorOr<DocumentAttachmentListResponseDto>>
{
    public async Task<ErrorOr<DocumentAttachmentListResponseDto>> Handle(
        GetInvoiceAttachmentsQuery request,
        CancellationToken cancellationToken)
    {
        var result = await documentService.GetAttachmentsAsync("Invoice", request.DocEntry, cancellationToken);
        return result;
    }
}
