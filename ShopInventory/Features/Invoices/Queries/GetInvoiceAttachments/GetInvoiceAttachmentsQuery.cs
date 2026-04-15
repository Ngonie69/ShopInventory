using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetInvoiceAttachments;

public sealed record GetInvoiceAttachmentsQuery(int DocEntry) : IRequest<ErrorOr<DocumentAttachmentListResponseDto>>;
