using System.Collections.Concurrent;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

public sealed class GetReportsHeadlineHandler(
    IReportService reportService,
    ILogger<GetReportsHeadlineHandler> logger
) : IRequestHandler<GetReportsHeadlineQuery, ErrorOr<ReportsHeadlineResult>>
{
    public async Task<ErrorOr<ReportsHeadlineResult>> Handle(
        GetReportsHeadlineQuery request,
        CancellationToken cancellationToken)
    {
        var period = request.Period;
        var unavailable = new ConcurrentBag<string>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(OverviewSection.Budget);

        var current = OverviewSection.LoadAsync("profit overview",
            token => reportService.GetProfitOverviewAsync(OverviewSection.Day(period.From), OverviewSection.Day(period.To), token),
            unavailable, logger, budget.Token, cancellationToken);
        var previous = OverviewSection.LoadAsync("previous period",
            token => reportService.GetProfitOverviewAsync(OverviewSection.Day(period.PreviousFrom), OverviewSection.Day(period.PreviousTo), token),
            unavailable, logger, budget.Token, cancellationToken);
        var aging = OverviewSection.LoadAsync("receivables",
            token => reportService.GetReceivablesAgingAsync(token),
            unavailable, logger, budget.Token, cancellationToken);

        await Task.WhenAll(current, previous, aging);

        if (current.Result is null && aging.Result is null)
        {
            return Error.Failure(
                "Reports.HeadlineUnavailable",
                "SAP did not answer for this period's figures. Try again in a minute.");
        }

        return new ReportsHeadlineResult(current.Result, previous.Result, aging.Result, unavailable.ToArray());
    }
}
