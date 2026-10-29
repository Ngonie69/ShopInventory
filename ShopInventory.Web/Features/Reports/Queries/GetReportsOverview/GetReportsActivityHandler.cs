using System.Collections.Concurrent;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

public sealed class GetReportsActivityHandler(
    IReportService reportService,
    ILogger<GetReportsActivityHandler> logger
) : IRequestHandler<GetReportsActivityQuery, ErrorOr<ReportsActivityResult>>
{
    /// <summary>The hub shows five of each; the full reports keep their own longer lists.</summary>
    public const int TopCount = 5;

    public async Task<ErrorOr<ReportsActivityResult>> Handle(
        GetReportsActivityQuery request,
        CancellationToken cancellationToken)
    {
        var period = request.Period;
        var from = OverviewSection.Day(period.From);
        var to = OverviewSection.Day(period.To);
        var unavailable = new ConcurrentBag<string>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(OverviewSection.Budget);

        // The chart reaches back further than a short period, so it reads its own range.
        var daily = OverviewSection.LoadAsync("daily sales",
            token => reportService.GetSalesSummaryAsync(OverviewSection.Day(period.ChartFrom), to, token),
            unavailable, logger, budget.Token, cancellationToken);
        var customers = OverviewSection.LoadAsync("top customers",
            token => reportService.GetTopCustomersAsync(from, to, TopCount, token),
            unavailable, logger, budget.Token, cancellationToken);
        var products = OverviewSection.LoadAsync("top products",
            token => reportService.GetTopProductsAsync(from, to, TopCount, token),
            unavailable, logger, budget.Token, cancellationToken);
        var payments = OverviewSection.LoadAsync("payments",
            token => reportService.GetPaymentSummaryAsync(from, to, token),
            unavailable, logger, budget.Token, cancellationToken);
        var orders = OverviewSection.LoadAsync("open orders",
            token => reportService.GetOrderFulfillmentAsync(from, to, cancellationToken: token),
            unavailable, logger, budget.Token, cancellationToken);
        var creditNotes = OverviewSection.LoadAsync("credit notes",
            token => reportService.GetCreditNoteSummaryAsync(from, to, token),
            unavailable, logger, budget.Token, cancellationToken);

        await Task.WhenAll(daily, customers, products, payments, orders, creditNotes);

        if (unavailable.Count == 6)
        {
            return Error.Failure(
                "Reports.ActivityUnavailable",
                "SAP did not answer for this period's sales. Try again in a minute.");
        }

        return new ReportsActivityResult(
            daily.Result, customers.Result, products.Result, payments.Result, orders.Result, creditNotes.Result,
            unavailable.ToArray());
    }
}
