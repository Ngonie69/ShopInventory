namespace ShopInventory.Features.CreditNotes;

/// <summary>What a SAP credit memo's lines were copied from.</summary>
/// <param name="InvoiceDocEntries">The distinct A/R invoices lines were copied from, in order.</param>
/// <param name="OtherBaseTypes">The distinct other document types lines were copied from, in order.</param>
public sealed record SapCreditNoteBaseLink(IReadOnlyList<int> InvoiceDocEntries, IReadOnlyList<int> OtherBaseTypes)
{
    /// <summary>The invoice, when every copied line came from the same one.</summary>
    public int? SingleInvoiceDocEntry => InvoiceDocEntries is [var only] ? only : null;
}
