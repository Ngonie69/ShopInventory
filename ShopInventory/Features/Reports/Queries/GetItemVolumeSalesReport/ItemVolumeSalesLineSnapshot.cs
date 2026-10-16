namespace ShopInventory.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// One invoice or credit-note line, flattened so invoices and credits can be aggregated together.
/// </summary>
/// <remarks>
/// <paramref name="Quantity"/> and the amounts are already signed — positive for an invoice,
/// negative for a credit note — so every total downstream is a plain sum and no aggregation has to
/// remember which way a credit points.
/// <para>
/// <paramref name="AmountUsd"/> is every line, whatever currency its document was raised in. SAP's
/// line <c>LineTotal</c> is in the company's local currency, which is USD, so a ZiG document's line
/// arrives already converted at that document's own rate — the conversion SAP's <c>DocTotal</c>
/// carries. <paramref name="AmountFromZig"/> is the part of it that came off ZiG documents, so a
/// reader can see how much of a partner's USD was invoiced in ZiG; it is never added on top.
/// </para>
/// </remarks>
internal sealed record ItemVolumeSalesLineSnapshot(
    string DocumentKey,
    bool IsCreditNote,
    string DocumentNumber,
    string DocumentEntry,
    DateTime DocumentDateUtc,
    string CardCode,
    string CardName,
    string Currency,
    int LineNumber,
    string ItemCode,
    string ItemName,
    decimal Quantity,
    decimal LineAmount,
    decimal AmountUsd,
    decimal AmountFromZig);
