using ShopInventory.Features.Invoices;

namespace ShopInventory.Features.CustomerDocuments.Documents;

/// <summary>
/// The fiscal link check's answer: whether a receipt can be printed on the invoice, which one, and
/// which record vouched for it.
/// </summary>
public sealed record FiscalLinkVerdict(
    FiscalLinkVerdictKind Kind,
    InvoicePdfReceipt? Receipt,
    string? Source,
    string? Reason)
{
    public static FiscalLinkVerdict Verified(InvoicePdfReceipt receipt, string source) =>
        new(FiscalLinkVerdictKind.Verified, receipt, source, null);

    public static FiscalLinkVerdict NotYet(string reason) =>
        new(FiscalLinkVerdictKind.NotYet, null, null, reason);

    public static FiscalLinkVerdict Mismatch(string reason) =>
        new(FiscalLinkVerdictKind.Mismatch, null, null, reason);
}
