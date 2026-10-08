using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.Invoices;

/// <summary>
/// An invoice rendered as the Fiscal Tax Invoice PDF, with the document it was rendered from.
/// </summary>
/// <param name="PdfBytes">The PDF.</param>
/// <param name="Invoice">What was printed, fiscal block included.</param>
/// <param name="SapInvoice">The SAP document as read, for the fields the printed DTO does not carry.</param>
/// <param name="FiscalQrCode">The QR payload printed, or null when the PDF has no QR.</param>
public sealed record ComposedInvoicePdf(
    byte[] PdfBytes,
    InvoiceDto Invoice,
    Invoice SapInvoice,
    string? FiscalQrCode);
