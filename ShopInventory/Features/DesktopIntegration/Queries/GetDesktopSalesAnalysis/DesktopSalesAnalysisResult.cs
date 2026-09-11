namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>
/// A breakdown of till takings over a period, one section per currency.
/// </summary>
/// <remarks>
/// Per currency because tills sell in both USD and ZWG, and a single total across them would add two
/// currencies together.
///
/// <para><c>WarehouseCode</c>: The warehouse the analysis was confined to, or null for every shop the
/// caller may read. Stated rather than echoed from the request: a caller confined to one shop is
/// narrowed to it without asking.</para>
/// <para><c>PaymentMethods</c>: The payment methods every breakdown is split by, in the order they
/// should be drawn. Cash, Swipe and Ecocash are always present, so a period in which one was never
/// taken says so with a zero rather than by leaving it out.</para>
/// </remarks>
public sealed record DesktopSalesAnalysisResult(
    DateTime FromDate,
    DateTime ToDate,
    string? WarehouseCode,
    string? SourceSystem,
    DateTime GeneratedAtUtc,
    List<string> PaymentMethods,
    List<DesktopSalesCurrencyAnalysis> Currencies);
