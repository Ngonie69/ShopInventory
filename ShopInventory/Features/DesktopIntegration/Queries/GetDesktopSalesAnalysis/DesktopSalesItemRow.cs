namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>What one item sold for across the period, in one line of business.</summary>
/// <remarks>
/// <para><c>Business</c>: The line of business that sold it, by its key in <c>SaleBusinesses</c>. Shops,
/// vending and vans sell different ranges, so an item sold by two of them is two rows, each ranked and
/// shared within its own business and never added to the other.</para>
/// <para><c>NetAmount</c>: The sum of its line totals, which are before VAT — VAT is charged per line
/// at the item's own rate and only stored on the sale, so an item's VAT-inclusive value cannot be
/// stated without re-deriving tax.</para>
/// <para><c>SalesCount</c>: How many sales included it, not how many lines.</para>
/// <para><c>ShareOfNetPercent</c>: Its share of the net value of every line its business sold in the
/// currency.</para>
/// </remarks>
public sealed record DesktopSalesItemRow(
    string ItemCode,
    string? ItemDescription,
    decimal Quantity,
    decimal NetAmount,
    int SalesCount,
    decimal ShareOfNetPercent,
    string Business);

/// <summary>What one line of business sold in a currency, of which its rows in <c>TopItems</c> are the head.</summary>
/// <remarks>
/// <para><c>Business</c>: Its key in <c>SaleBusinesses</c>; <c>Label</c> is what a page calls it.</para>
/// <para><c>DistinctItems</c>: How many different items it sold.</para>
/// <para><c>NetAmount</c>: The net value of every line it sold, which is what its items' shares are of.</para>
/// </remarks>
public sealed record DesktopSalesBusinessItems(
    string Business,
    string Label,
    int DistinctItems,
    decimal QuantitySold,
    decimal NetAmount);
