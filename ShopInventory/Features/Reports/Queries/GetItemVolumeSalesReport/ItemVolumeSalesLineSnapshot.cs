namespace ShopInventory.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// One invoice or credit-note line, flattened so invoices and credits can be aggregated together.
/// </summary>
/// <remarks>
/// <paramref name="Quantity"/> and the amounts are already signed — positive for an invoice,
/// negative for a credit note — so every total downstream is a plain sum and no aggregation has to
/// remember which way a credit points.
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
    decimal AmountZig);
