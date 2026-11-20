using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.RouteCustomers.Queries.ExportRouteCustomerActivity;

/// <summary>
/// Reads the sales summary for the export's window — with a month history when "By month" is wanted — cuts
/// it to what was asked for, and lays the workbook out.
/// </summary>
/// <remarks>
/// The summary is read again rather than taken from the page: the page holds no month history, and reading
/// both at the moment of export keeps every sheet on the same figures.
/// </remarks>
public sealed class ExportRouteCustomerActivityHandler(
    IRouteCustomerService routeCustomers,
    IReportExportService export,
    TimeProvider? clock = null)
    : IRequestHandler<ExportRouteCustomerActivityQuery, ErrorOr<byte[]>>
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ErrorOr<byte[]>> Handle(ExportRouteCustomerActivityQuery request, CancellationToken cancellationToken)
    {
        var windowDays = RouteCustomerActivity.NormalizeWindow(request.WindowDays);
        var today = _clock.GetLocalNow().Date;
        var wantsHistory = request.Sheets.HasFlag(RouteCustomerActivitySheets.ByMonth);

        try
        {
            var summary = await routeCustomers.GetSalesSummaryAsync(
                from: RouteCustomerActivity.WindowStart(today, windowDays),
                to: today,
                dormantDays: RouteCustomerActivity.DormantDaysFor(windowDays),
                includeInactive: true,
                monthsOfHistory: wantsHistory ? RouteCustomerActivity.HistoryMonths : 0);

            var rows = SelectRows(summary, request.OnlyCustomerIds, request.IncludeRemoved);

            var details = request.Customers
                .GroupBy(customer => customer.Id)
                .ToDictionary(group => group.Key, group => group.First());

            var report = new RouteCustomerActivityReport(
                summary, windowDays, rows, details, request.RouteLabels, request.ScopeDescription, request.Sheets, today);

            // Laying out thousands of rows takes seconds of CPU. Off the circuit's own thread, so the page
            // stays responsive — and shows its spinner — while the file is built.
            return await Task.Run(() => export.ExportRouteCustomerActivityToExcel(report), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error.Failure("RouteCustomerActivity.ExportFailed", ex.Message);
        }
    }

    /// <summary>
    /// The current view is the page's own customers, by id — the page has already applied its route, status,
    /// activity and search filters. Every customer means every row, less the removed and the deleted unless
    /// those were asked for.
    /// </summary>
    internal static List<RouteCustomerSalesRowModel> SelectRows(
        RouteCustomerSalesSummaryModel summary,
        IReadOnlySet<int>? onlyCustomerIds,
        bool includeRemoved)
    {
        var rows = summary.Routes.SelectMany(route => route.Customers);

        rows = onlyCustomerIds is not null
            ? rows.Where(row => row.RouteCustomerId is { } id && onlyCustomerIds.Contains(id))
            : rows.Where(row => includeRemoved || (row.IsActive && !row.IsDeleted));

        return rows.ToList();
    }
}
