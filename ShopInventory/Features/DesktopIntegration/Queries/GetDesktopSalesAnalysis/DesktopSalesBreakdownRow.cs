namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>
/// The takings of one shop, one business partner, one source, or one operator, split by payment method.
/// </summary>
/// <remarks>
/// <para><c>Key</c>: What the row groups on — a warehouse code, a business partner's CardCode, a source
/// system, or an account id.</para>
/// <para><c>Label</c>: What to call it on a page. A business partner is called by the name its sales
/// carried, so a document lists "Farm Counter Sales" rather than a code the reader has to look up.</para>
/// <para><c>ByPaymentMethod</c>: One entry per method in the analysis' column order, zeros
/// included.</para>
/// </remarks>
public sealed record DesktopSalesBreakdownRow(
    string Key,
    string Label,
    int SalesCount,
    decimal TotalAmount,
    decimal VatAmount,
    decimal ShareOfValuePercent,
    List<DesktopSalesPaymentAmount> ByPaymentMethod);
