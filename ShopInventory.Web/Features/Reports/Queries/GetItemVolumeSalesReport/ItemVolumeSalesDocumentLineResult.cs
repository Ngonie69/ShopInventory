namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// A single invoice or credit-note line, so the totals above can be reconciled to documents.
/// </summary>
public sealed class ItemVolumeSalesDocumentLineResult
{
    public string PeriodLabel { get; set; } = string.Empty;

    /// <summary>"Invoice" or "Credit Note".</summary>
    public string DocumentType { get; set; } = string.Empty;

    public DateTime DocumentDateUtc { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public string DocumentEntry { get; set; } = string.Empty;
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;

    public int LineNumber { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Positive on an invoice line, negative on a credit-note line.</summary>
    public decimal Quantity { get; set; }

    public decimal? VolumeFactor { get; set; }
    public decimal Volume { get; set; }

    public decimal LineAmount { get; set; }
    public decimal AmountUsd { get; set; }
    public decimal AmountZig { get; set; }
}
