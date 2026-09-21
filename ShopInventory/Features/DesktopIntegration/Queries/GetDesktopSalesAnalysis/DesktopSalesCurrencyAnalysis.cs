namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>
/// Every figure the analysis states for the sales made in one currency.
/// </summary>
/// <remarks>
/// <para><c>TotalAmount</c>: What the sales came to, VAT included.</para>
/// <para><c>NetAmount</c>: The same, before VAT.</para>
/// <para><c>QuantitySold</c>: Units across every line, whatever the item.</para>
/// <para><c>DaysTraded</c>: Days in the period with at least one sale.</para>
/// <para><c>DistinctItems</c>: How many different items sold, of which <paramref name="TopItems"/> is
/// the head.</para>
/// <para><c>ByBusinessPartner</c>: The same takings by the business partner each sale was made as, keyed
/// by CardCode and labelled by the partner's name. One shop can sell as several partners, so this is
/// not <c>ByWarehouse</c> renamed.</para>
/// <para><c>PreviousSalesCount</c>/<c>PreviousTotalAmount</c>: The same currency's sales over the same
/// number of days just before the period. Zero means none, which a page must not state as a change.</para>
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
    decimal PreviousTotalAmount);
