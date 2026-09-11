namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>The takings rung up in one hour of the day, summed across the period.</summary>
/// <remarks>
/// <para><c>Hour</c>: The hour in CAT, 0–23 — the counter's own clock, which is what "how busy is
/// lunchtime" means. Sales are stamped in UTC.</para>
/// </remarks>
public sealed record DesktopSalesHourRow(
    int Hour,
    int SalesCount,
    decimal TotalAmount);
