using ShopInventory.Web.Services;

namespace ShopInventory.Web.Common;

/// <summary>
/// The day-over-day figures more than one dashboard reports.
///
/// These lived inline on the old combined dashboard. The administrator's page
/// still shows them and the cashier's page will lead with them, so they sit
/// here rather than being copied a second and third time — and
/// <see cref="BuildTrend"/> becomes something a test can reach.
/// </summary>
public static class DashboardFigures
{
    /// <summary>The invoice date-range API clamps a page to 100 rows.</summary>
    private const int InvoicePageSize = 100;

    /// <summary>
    /// How far the value total will chase a busy day. A day past a thousand
    /// invoices reports the count in full and the value of the first thousand;
    /// walking further costs more than the figure is worth on a page that is
    /// read at a glance.
    /// </summary>
    private const int MaxInvoiceStatPages = 10;

    /// <summary>
    /// Invoices raised on one day, and what they came to.
    /// </summary>
    /// <param name="includeValue">
    /// False asks only for the count, with the smallest page that still returns
    /// an authoritative total — which is all a comparison day needs.
    /// </param>
    public static async Task<(int Count, decimal Total)> GetInvoiceDayTotalsAsync(
        IInvoiceService invoiceService,
        DateTime date,
        bool includeValue)
    {
        ArgumentNullException.ThrowIfNull(invoiceService);

        var response = await invoiceService.GetInvoicesByDateRangeAsync(
            date, date, page: 1, pageSize: includeValue ? InvoicePageSize : 1);

        if (response == null) return (0, 0m);

        // TotalCount comes from a separate count query, so it is accurate even
        // though the API clamps a page to 100 rows.
        var count = response.TotalCount;
        if (!includeValue) return (count, 0m);

        var total = response.Invoices?.Sum(invoice => invoice.DocTotal) ?? 0m;
        var fetched = response.Invoices?.Count ?? 0;

        // A day busier than one page needs following pages before the value
        // total covers the same invoices the count describes.
        for (var page = 2; fetched < count && page <= MaxInvoiceStatPages; page++)
        {
            var next = await invoiceService.GetInvoicesByDateRangeAsync(
                date, date, page: page, pageSize: InvoicePageSize);

            if (next?.Invoices is not { Count: > 0 }) break;

            total += next.Invoices.Sum(invoice => invoice.DocTotal);
            fetched += next.Invoices.Count;
        }

        return (count, total);
    }

    /// <summary>Payments received on one day. The endpoint is unpaged, so one call covers it.</summary>
    public static async Task<(int Count, decimal Total)> GetPaymentDayTotalsAsync(
        IPaymentService paymentService,
        DateTime date)
    {
        ArgumentNullException.ThrowIfNull(paymentService);

        var response = await paymentService.GetPaymentsByDateAsync(date);
        if (response?.Payments == null) return (0, 0m);

        return (response.Payments.Count, response.Payments.Sum(payment => payment.DocTotal));
    }

    /// <summary>
    /// Day-over-day change in document count. Returns no text when neither day
    /// has any documents, and an absolute delta when yesterday gives nothing to
    /// divide by.
    /// </summary>
    public static (string? Text, int Direction) BuildTrend(int today, int yesterday)
    {
        if (today == 0 && yesterday == 0) return (null, 0);
        if (yesterday == 0) return ($"+{today}", 1);

        var change = Math.Round((today - yesterday) / (double)yesterday * 100d);
        return ($"{Math.Abs(change):N0}%", Math.Sign(change));
    }
}
