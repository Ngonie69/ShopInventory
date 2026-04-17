using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseOrders.Commands.UploadPurchaseOrderDocument;

public sealed record UploadPurchaseOrderDocumentCommand(
    string PoReferenceNumber,
    byte[] FileBytes,
    string FileName,
    string ContentType,
    string? Description,
    Guid UserId
) : IRequest<ErrorOr<DocumentAttachmentDto>>;
