using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Features.Documents;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Queries.GetInvoiceAttachments;

public sealed class GetInvoiceAttachmentsHandler(
    DocumentAttachmentAccessService attachmentAccessService,
    IDocumentService documentService
) : IRequestHandler<GetInvoiceAttachmentsQuery, ErrorOr<DocumentAttachmentListResponseDto>>
{
    public async Task<ErrorOr<DocumentAttachmentListResponseDto>> Handle(
        GetInvoiceAttachmentsQuery request,
        CancellationToken cancellationToken)
    {
        var accessResult = await attachmentAccessService.AuthorizeEntityAccessAsync(
            "Invoice",
            request.DocEntry,
            false,
            cancellationToken);

        if (accessResult.IsError)
        {
            return accessResult.Errors;
        }

        var result = await documentService.GetAttachmentsAsync("Invoice", request.DocEntry, cancellationToken);
        return result;
    }
}
