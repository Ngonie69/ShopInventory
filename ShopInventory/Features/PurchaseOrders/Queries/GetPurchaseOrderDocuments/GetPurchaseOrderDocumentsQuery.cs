using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrderDocuments;

public sealed record GetPurchaseOrderDocumentsQuery(
    string? PoReferenceNumber = null
) : IRequest<ErrorOr<DocumentAttachmentListResponseDto>>;
