using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility;

/// <summary>
/// The date range a handset asked for, in both of the forms its history is read against.
/// </summary>
/// <remarks>
/// The handset sends bare trading days — "2026-08-12", always a seven day span — and means them in
/// CAT, the only clock it has. Two consumers want that range and they do not want it the same way.
///
/// <para>
/// The local tables hold instants: <c>SalesOrders.OrderDate</c> and
/// <c>DesktopFiscalTransactions.TimestampUtc</c> are <c>timestamp with time zone</c>, stamped from
/// <c>DateTime.UtcNow</c>. Comparing a trading day against one of those means converting it, and CAT
/// is two hours ahead, so a sale made at 00:30 on the 12th is stored at 22:30 on the 11th. Filtering
/// on the bare date would drop it out of the 12th's history — the boundary a bare
/// <c>SpecifyKind(Utc)</c> quietly gets wrong in both directions, losing the first two hours of the
/// opening day and gaining the first two of the day after the closing one.
/// </para>
///
/// <para>
/// SAP wants the opposite. <c>GetInvoiceHeadersByDateRangeAsync</c> filters <c>DocDate</c> as a
/// calendar date formatted <c>yyyy-MM-dd</c>, in SAP's own CAT terms, so it takes the trading days
/// unconverted. Handing it the UTC instants would move each bound back a day at the boundary.
/// </para>
///
/// Same reasoning, and the same half-open shape, as
/// <see cref="RouteCustomers.Queries.RouteCustomerSalesReporting.ToUtcWindow"/>.
/// </remarks>
public readonly record struct VanSalesLegacyDateWindow(DateTime? FromDate, DateTime? ToDate)
{
    /// <summary>
    /// Reads the pair of legacy date strings a history request carries. Either may be absent, and an
    /// absent bound leaves that end of the range open.
    /// </summary>
    public static VanSalesLegacyDateWindow Parse(string? startDate, string? endDate) =>
        new(
            VanSalesCompatibilityMapper.ParseLegacyDate(startDate)?.Date,
            VanSalesCompatibilityMapper.ParseLegacyDate(endDate)?.Date);

    /// <summary>First instant of the opening trading day, as a <c>timestamptz</c> column stores it.</summary>
    public DateTime? FromUtc => FromDate.HasValue ? AuditService.FromCAT(FromDate.Value) : null;

    /// <summary>
    /// First instant of the day after the closing trading day. Half-open because the alternative is a
    /// tick-precision inclusive bound that rounds differently in the database than it does here.
    /// </summary>
    public DateTime? ToUtcExclusive => ToDate.HasValue ? AuditService.FromCAT(ToDate.Value.AddDays(1)) : null;
}
