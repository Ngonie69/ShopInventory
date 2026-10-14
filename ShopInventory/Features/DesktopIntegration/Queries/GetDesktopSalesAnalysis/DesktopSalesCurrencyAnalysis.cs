namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>
/// Every figure the analysis states for the sales made in one currency.
/// </summary>
/// <remarks>
/// <para><c>TotalAmount</c>: What the sales came to, VAT included.</para>
/// <para><c>NetAmount</c>: The same, before VAT.</para>
/// <para><c>QuantitySold</c>: Units across every line, whatever the item.</para>
/// <para><c>DaysTraded</c>: Days in the period with at least one sale.</para>
/// <para><c>DistinctItems</c>: How many different items sold, whichever line of business sold them.</para>
/// <para><c>TopItems</c>: The best sellers of each line of business, one business after another and never
/// ranked together: each row names its business, and its share is of that business's lines alone.
/// <c>ItemsByBusiness</c> says how many items each business sold in all.</para>
/// <para><c>ByBusinessPartner</c>: The same takings by the business partner each sale was made as, keyed
/// by CardCode and labelled by the partner's name. One shop can sell as several partners, so this is
/// not <c>ByWarehouse</c> renamed.</para>
/// <para><c>PreviousSalesCount</c>/<c>PreviousTotalAmount</c>: The same currency's sales over the same
/// number of days just before the period. Zero means none, which a page must not state as a change.</para>
/// <para><b>Credits.</b> Every figure above is what was sold: a sale the office later credited back is
/// still counted, at what it was rung up for, on the day it was sold. What was credited is stated
/// beside it — here and on each breakdown row — so a credited sale reads as a correction and not as a
/// smaller sale. The comparison with the period before is sold against sold.</para>
/// </remarks>
public sealed record DesktopSalesCurrencyAnalysis(
    string Currency,
    int SalesCount,
    decimal TotalAmount,
    decimal VatAmount,
    decimal NetAmount,
    decimal AverageSale,
    decimal QuantitySold,
    int DaysTraded,
    int DistinctItems,
    List<DesktopSalesPaymentMethodRow> ByPaymentMethod,
    List<DesktopSalesDayRow> ByDay,
    List<DesktopSalesHourRow> ByHour,
    List<DesktopSalesBreakdownRow> ByWarehouse,
    List<DesktopSalesBreakdownRow> ByBusinessPartner,
    List<DesktopSalesBreakdownRow> BySource,
    List<DesktopSalesBreakdownRow> ByOperator,
    List<DesktopSalesItemRow> TopItems,
    int PreviousSalesCount,
    decimal PreviousTotalAmount)
{
    /// <summary>
    /// Van analysis only: the takings by the business partner each van bills as — the document's own
    /// CardCode — keyed and labelled by that code. Empty on the desktop analysis.
    /// </summary>
    /// <remarks>
    /// Not <c>ByWarehouse</c> renamed: a van can invoice under another van's account, so the warehouse
    /// it sold from and the partner it billed need not match.
    /// </remarks>
    public List<DesktopSalesBreakdownRow> ByVanAccount { get; init; } = [];

    /// <summary>
    /// One row per line of business that sold anything, in the order <c>TopItems</c> lists them.
    /// </summary>
    public List<DesktopSalesBusinessItems> ItemsByBusiness { get; init; } = [];

    /// <summary>
    /// What has been credited back on the period's sales, tax-inclusive like <c>TotalAmount</c>. A credit
    /// is counted against the day of the sale it reverses, whenever it was raised.
    /// </summary>
    public decimal CreditedAmount { get; init; }

    /// <summary><c>TotalAmount</c> less <see cref="CreditedAmount"/>: what the sales stand at.</summary>
    public decimal NetOfCredits => TotalAmount - CreditedAmount;

    /// <summary>How many of the sales have had anything credited back.</summary>
    public int CreditedSalesCount { get; init; }

    /// <summary>How many of those were credited back in full.</summary>
    public int FullyCreditedSalesCount { get; init; }

    /// <summary>
    /// Units credited back, of <c>QuantitySold</c>, where a credit could be tied to a line of its sale.
    /// </summary>
    public decimal CreditedQuantity { get; init; }
}
