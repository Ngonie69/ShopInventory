using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Queries.PreviewInvoiceWhatsAppDocument;

/// <summary>
/// Exactly what sending an invoice on WhatsApp would send right now — or why it would wait.
/// </summary>
public sealed record PreviewInvoiceWhatsAppDocumentQuery(int DocEntry) : IRequest<ErrorOr<InvoiceWhatsAppPreviewDto>>;
