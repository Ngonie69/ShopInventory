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
/// <para><b>Credits.</b> <c>Quantity</c> and <c>NetAmount</c> are what was sold, and the rank and the
/// share are of that. What was credited back of the item is beside them, with what is left.</para>
/// </remarks>
public sealed record DesktopSalesItemRow(
    string ItemCode,
    string? ItemDescription,
    decimal Quantity,
    decimal NetAmount,
    int SalesCount,
    decimal ShareOfNetPercent,
    string Business)
{
    /// <summary>Units of <c>Quantity</c> credited back; 0 when none were.</summary>
    public decimal CreditedQuantity { get; init; }

    /// <summary>
    /// The part of <c>NetAmount</c> those units were sold for — before VAT, like <c>NetAmount</c>, and so
    /// not the tax-inclusive figure a sale's credited amount is.
    /// </summary>
    public decimal CreditedNetAmount { get; init; }

    /// <summary><c>Quantity</c> less <see cref="CreditedQuantity"/>.</summary>
    public decimal QuantityAfterCredits => Quantity - CreditedQuantity;

    /// <summary><c>NetAmount</c> less <see cref="CreditedNetAmount"/>: the item's line value after credits, before VAT.</summary>
    public decimal NetAmountAfterCredits => NetAmount - CreditedNetAmount;

    /// <summary>
    /// SAP's booked gross profit on the item's lines of the invoices its sales were posted as: their value
    /// before VAT less the stock cost SAP relieved when the goods left. Null when margin was not asked for,
    /// SAP could not be read, or none of the item's sales has reached SAP yet. Negative is a loss.
    /// </summary>
    public decimal? GrossProfit { get; init; }

    /// <summary>
    /// The value before VAT that <see cref="GrossProfit"/> was measured on: the part of <c>NetAmount</c>
    /// SAP holds an invoice for. Below <c>NetAmount</c> while some of the item's sales are still unposted.
    /// </summary>
    public decimal? CostedNetAmount { get; init; }

    /// <summary><see cref="GrossProfit"/> as a percentage of <see cref="CostedNetAmount"/>, to one place.</summary>
    public decimal? MarginPercent => GrossProfit is { } profit && CostedNetAmount is { } costed && costed != 0
        ? Math.Round(profit / costed * 100m, 1, MidpointRounding.AwayFromZero)
        : null;
}

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
    decimal NetAmount)
{
    /// <summary>Units of <c>QuantitySold</c> credited back.</summary>
    public decimal CreditedQuantity { get; init; }

    /// <summary>The part of <c>NetAmount</c> those units were sold for, before VAT.</summary>
    public decimal CreditedNetAmount { get; init; }

    /// <summary><c>NetAmount</c> less <see cref="CreditedNetAmount"/>.</summary>
    public decimal NetAmountAfterCredits => NetAmount - CreditedNetAmount;

    /// <summary>SAP's booked gross profit on every item it sold, not only the listed head; null as on an item row.</summary>
    public decimal? GrossProfit { get; init; }

    /// <summary>The value before VAT <see cref="GrossProfit"/> was measured on.</summary>
    public decimal? CostedNetAmount { get; init; }

    /// <summary><see cref="GrossProfit"/> as a percentage of <see cref="CostedNetAmount"/>, to one place.</summary>
    public decimal? MarginPercent => GrossProfit is { } profit && CostedNetAmount is { } costed && costed != 0
        ? Math.Round(profit / costed * 100m, 1, MidpointRounding.AwayFromZero)
        : null;
}
