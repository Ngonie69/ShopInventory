namespace ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;

// ── Order to actual sale ─────────────────────────────────────────────────────────
//
// An ADR raises sales orders; the van turns them into invoices. These records follow every order to
// the van sale it became — through the invoice queue entry that carries the order's id and the sale's
// reference — and say how much of what was ordered was actually sold, and why the rest was not.
//
// Money is per currency throughout and includes VAT on both sides: an order's DocTotal is its subtotal
// less discount plus tax, and every van sale fact is stated with VAT.

/// <summary>
/// The ADRs' orders followed to the sales they became, for the period and the one before it.
/// </summary>
/// <remarks>
/// <c>Overall</c> is every ADR whatever the query narrows to, as the contribution figures are.
/// <c>Adrs</c>, <c>Days</c>, <c>Shops</c>, <c>Items</c> and <c>Orders</c> follow the query's ADR when
/// it names one. <c>Previous</c> is null when the comparison was not asked for.
/// </remarks>
public sealed record AdrConversionReportResult(
    AdrConversionSummaryResult Overall,
    List<AdrConversionRowResult> Adrs,
    List<AdrConversionDayResult> Days,
    List<AdrConversionShopResult> Shops,
    List<AdrConversionItemResult> Items,
    List<AdrLedgerOrderResult> Orders,
    AdrConversionComparisonResult? Previous);

/// <summary>The period before, measured the same way, for the deltas beside every figure.</summary>
public sealed record AdrConversionComparisonResult(
    DateTime FromDate,
    DateTime ToDate,
    AdrConversionSummaryResult Overall,
    List<AdrConversionRowResult> Adrs);

/// <summary>One ADR's conversion, with the route they are assigned to.</summary>
public sealed record AdrConversionRowResult(
    Guid UserId,
    string? RouteCode,
    string? RouteName,
    AdrConversionSummaryResult Summary);

/// <summary>
/// A set of orders followed to the sale: how many reached each stage, the money per currency, and
/// how quickly the van invoiced them.
/// </summary>
/// <remarks>
/// <c>MedianDaysToInvoice</c> and <c>InvoicedByNextDay</c> are over the invoiced orders and null when
/// there are none. <c>Calls</c> is null when nobody recorded a visit — unmeasured, not zero.
/// </remarks>
public sealed record AdrConversionSummaryResult(
    AdrFunnelCountsResult Counts,
    List<AdrConversionMoneyResult> Money,
    double? MedianDaysToInvoice,
    double? InvoicedByNextDay,
    int? Calls,
    int ProductiveCalls)
{
    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;
}

/// <summary>
/// Where a set of orders ended up. Each order is in exactly one of the six outcomes, so
/// <c>InvoicedInFull + ShortSupplied + Repriced + NoLinkedInvoice + NotYetInSap + Cancelled == Ordered</c>.
/// <c>ReachedSap</c> counts orders with a SAP number or an invoice, and <c>Invoiced</c> the first three.
/// </summary>
public sealed record AdrFunnelCountsResult(
    int Ordered,
    int ReachedSap,
    int Invoiced,
    int InvoicedInFull,
    int ShortSupplied,
    int Repriced,
    int NoLinkedInvoice,
    int NotYetInSap,
    int Cancelled);

/// <summary>
/// One currency's orders, what they became, and where the difference went.
/// </summary>
/// <remarks>
/// <para>The five reasons add up to the gap: <c>Ordered − Invoiced == ShortSupplied + Repriced +
/// NoLinkedInvoice + NotYetInSap + Cancelled</c>. <c>Repriced</c> is what is left on invoiced orders
/// once the short supply is taken out, and is negative where the van sold more than was ordered.</para>
/// <para><c>ShortSupplied</c> values the units the van did not deliver at the order's own prices.</para>
/// <para><c>VanSales</c> is everything the vans that served these orders took in the period, and
/// <c>AdrLedVanSales</c> the part of it that came from these orders. Both are sale-dated, unlike the
/// order-dated figures above, because a share of a van's takings has to be over one set of days.</para>
/// </remarks>
public sealed record AdrConversionMoneyResult(
    string Currency,
    decimal Ordered,
    decimal ReachedSap,
    decimal Invoiced,
    decimal ShortSupplied,
    decimal Repriced,
    decimal NoLinkedInvoice,
    decimal NotYetInSap,
    decimal Cancelled,
    decimal OrderedOnInvoiced,
    decimal VanSales,
    decimal AdrLedVanSales)
{
    public decimal Gap => Ordered - Invoiced;

    /// <summary>Null, not zero, when nothing was ordered in this currency.</summary>
    public double? Conversion => Ordered > 0 ? (double)(Invoiced / Ordered) : null;

    /// <summary>The share of the invoiced orders' value the van delivered.</summary>
    public double? FillRate => OrderedOnInvoiced > 0 ? 1 - (double)(ShortSupplied / OrderedOnInvoiced) : null;

    public double? VanShare => VanSales > 0 ? (double)(AdrLedVanSales / VanSales) : null;

    /// <summary>SAP's booked gross profit on the invoices; null when none is costed or not read.</summary>
    public decimal? GrossProfit { get; init; }

    public decimal? CostedNetAmount { get; init; }

    public decimal? MarginPercent => GrossProfit is { } profit && CostedNetAmount is { } costed && costed != 0
        ? decimal.Round(profit / costed * 100m, 1, MidpointRounding.AwayFromZero)
        : null;
}

/// <summary>
/// One ADR's trading day. <c>Calls</c> is null when they recorded no visit that day.
/// </summary>
public sealed record AdrConversionDayResult(
    Guid UserId,
    DateTime TradingDate,
    int? Calls,
    int ProductiveCalls,
    int Orders,
    List<AdrDayMoneyResult> Money);

/// <summary>One currency of a day: the orders captured that day, and what they became.</summary>
public sealed record AdrDayMoneyResult(
    string Currency,
    decimal Ordered,
    decimal Invoiced,
    decimal ShortSupplied);

/// <summary>
/// A shop an ADR ordered for or visited. <c>Visits</c> are days checked in; a shop visited and never
/// ordered for is listed with no orders, because a call that bought nothing is part of the picture.
/// <c>Uninvoiced</c> counts orders with no invoice yet, whether or not they reached SAP.
/// </summary>
public sealed record AdrConversionShopResult(
    Guid UserId,
    string CustomerCode,
    string? CustomerName,
    int Visits,
    int Orders,
    int Invoiced,
    int ShortSupplied,
    int Uninvoiced,
    List<AdrDayMoneyResult> Money,
    DateTime? LastOrderOn);

/// <summary>
/// An item an ADR ordered, against what the van delivered.
/// </summary>
/// <remarks>
/// <c>LineCount</c> and <c>ShopCount</c> are over every live order. The quantities are over the
/// invoiced orders only, so an order still waiting for the van does not read as short. Van sale lines
/// carry no unit, so items are matched on the item code and the order's unit is the one shown.
/// </remarks>
public sealed record AdrConversionItemResult(
    Guid UserId,
    string ItemCode,
    string? ItemDescription,
    string? UoMCode,
    int LineCount,
    int ShopCount,
    decimal OrderedQuantity,
    decimal SuppliedQuantity,
    decimal ShortQuantity,
    int ShortShopCount,
    List<AdrItemMoneyResult> Money)
{
    public double? FillRate => OrderedQuantity > 0 ? 1 - (double)(ShortQuantity / OrderedQuantity) : null;
}

/// <summary>One currency of an item: its share of the orders' value, and of the short supply.</summary>
public sealed record AdrItemMoneyResult(
    string Currency,
    decimal Ordered,
    decimal ShortSupplied);

/// <summary>
/// One order beside the invoice it became.
/// </summary>
/// <remarks>
/// <c>Outcome</c> is one of <see cref="AdrOutcomes"/>. The invoice columns are null until there is an
/// invoice. <c>InvoiceDocNum</c> is SAP's number for it once the queue has posted it.
/// </remarks>
public sealed record AdrLedgerOrderResult(
    int Id,
    string OrderNumber,
    Guid UserId,
    DateTime TradingDate,
    DateTime CapturedAtCat,
    string? CustomerCode,
    string? CustomerName,
    string Currency,
    int LineCount,
    decimal Ordered,
    int? SapOrderDocNum,
    string Outcome,
    string? InvoiceReference,
    int? InvoiceDocNum,
    DateTime? InvoicedAtCat,
    decimal? Invoiced,
    double? DaysToInvoice,
    decimal ShortSupplied,
    decimal Repriced)
{
    public decimal? Variance => Invoiced is { } invoiced ? invoiced - Ordered : null;
}

/// <summary>The six places an order can end up.</summary>
public static class AdrOutcomes
{
    public const string InvoicedInFull = "Invoiced in full";
    public const string ShortSupplied = "Short-supplied";
    public const string Repriced = "Repriced";
    public const string NoLinkedInvoice = "No linked invoice";
    public const string NotYetInSap = "Not yet in SAP";
    public const string Cancelled = "Cancelled";
}
