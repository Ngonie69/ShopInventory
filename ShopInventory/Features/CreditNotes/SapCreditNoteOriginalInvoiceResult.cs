using ShopInventory.Models;

namespace ShopInventory.Features.CreditNotes;

/// <summary>The invoice a SAP credit memo reverses, or why it has none.</summary>
/// <param name="Invoice">The invoice as SAP holds it: at least its DocEntry, DocNum and Comments.</param>
/// <param name="Refusal">A sentence, starting lower case, saying why no invoice could be named.</param>
public sealed record SapCreditNoteOriginalInvoiceResult(Invoice? Invoice, string? Refusal)
{
    public static SapCreditNoteOriginalInvoiceResult Found(Invoice invoice) => new(invoice, null);

    public static SapCreditNoteOriginalInvoiceResult Refused(string refusal) => new(null, refusal);
}
