namespace ShopInventory.DTOs;

/// <summary>
/// Exactly what sending an invoice would send now — the PDF, its name and caption — or why it cannot go yet.
/// </summary>
public sealed class InvoiceWhatsAppPreviewDto
{
    public bool Ready { get; set; }

    /// <summary>Why it is not ready: no fiscal receipt yet, one that belongs to another document, and so on.</summary>
    public string? Reason { get; set; }

    public string? FileName { get; set; }

    public string? Caption { get; set; }

    public string? FiscalEvidenceSource { get; set; }

    public string? PdfBase64 { get; set; }
}
