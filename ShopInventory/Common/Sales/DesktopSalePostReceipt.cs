namespace ShopInventory.Common.Sales;

/// <summary>
/// The SAP invoice a completed post left behind, held so a repeat can be answered with the document
/// rather than with "already done".
/// </summary>
/// <remarks>
/// Deliberately the document numbers and nothing else. A claim outlives the request that made it —
/// <c>SecuritySettings.IdempotencyKeyExpirationMinutes</c>, an hour by default — and anything richer
/// stored here would be a second copy of the sale, able to disagree with the row.
/// </remarks>
public sealed record DesktopSalePostReceipt(int SapDocEntry, int SapDocNum);
