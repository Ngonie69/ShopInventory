namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>What one item sold for across the period.</summary>
/// <remarks>
/// <para><c>NetAmount</c>: The sum of its line totals, which are before VAT — VAT is charged per line
/// at the item's own rate and only stored on the sale, so an item's VAT-inclusive value cannot be
/// stated without re-deriving tax.</para>
/// <para><c>SalesCount</c>: How many sales included it, not how many lines.</para>
/// <para><c>ShareOfNetPercent</c>: Its share of the net value of every line in the currency.</para>
/// </remarks>
public sealed record DesktopSalesItemRow(
    string ItemCode,
    string? ItemDescription,
    decimal Quantity,
    decimal NetAmount,
    int SalesCount,
    decimal ShareOfNetPercent);
