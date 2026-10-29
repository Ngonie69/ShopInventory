using System.Collections.Concurrent;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

public sealed class GetReportsStockWatchHandler(
    IReportService reportService,
    ILogger<GetReportsStockWatchHandler> logger
) : IRequestHandler<GetReportsStockWatchQuery, ErrorOr<ReportsStockWatchResult>>
{
    public async Task<ErrorOr<ReportsStockWatchResult>> Handle(
        GetReportsStockWatchQuery request,
        CancellationToken cancellationToken)
    {
        var unavailable = new ConcurrentBag<string>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(OverviewSection.Budget);

        var lowStock = OverviewSection.LoadAsync("low stock",
            token => reportService.GetLowStockAlertsAsync(cancellationToken: token),
            unavailable, logger, budget.Token, cancellationToken);
        var slowMoving = OverviewSection.LoadAsync("slow-moving stock",
            token => reportService.GetSlowMovingProductsAsync(
                OverviewSection.Day(request.Today.AddDays(-GetReportsStockWatchQuery.SalesLookbackDays)),
                OverviewSection.Day(request.Today),
                GetReportsStockWatchQuery.SlowMovingDays,
                token),
            unavailable, logger, budget.Token, cancellationToken);

        await Task.WhenAll(lowStock, slowMoving);

        if (lowStock.Result is null && slowMoving.Result is null)
        {
            return Error.Failure("Reports.StockUnavailable", "SAP did not answer for stock. Try again in a minute.");
        }

        return new ReportsStockWatchResult(lowStock.Result, slowMoving.Result, unavailable.ToArray());
    }
}
