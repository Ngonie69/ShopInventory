using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Vending.Queries.ExportVendorPerformance;

/// <summary>
/// Reads both periods fresh from the API and builds the workbook from them.
/// </summary>
/// <remarks>
/// Not from the figures already on the page: those cover one period, and the comparison needs the one
/// before. Vendor money comes from the same sales summary the page reads, one depot at a time as the
/// page does, so the workbook's totals are the page's. Item sales come in one read per period across
/// every depot.
/// </remarks>
public sealed class ExportVendorPerformanceHandler(
    IVendingService vendingService,
    IRouteCustomerService routeCustomerService,
    IMasterDataCacheService masterDataCache,
    IReportExportService reportExport,
    ILogger<ExportVendorPerformanceHandler> logger
) : IRequestHandler<ExportVendorPerformanceQuery, ErrorOr<ExportVendorPerformanceResult>>
{
    public async Task<ErrorOr<ExportVendorPerformanceResult>> Handle(
        ExportVendorPerformanceQuery request,
        CancellationToken cancellationToken)
    {
        var from = request.From.Date;
        var to = request.To.Date < from ? from : request.To.Date;
        var (previousFrom, previousTo) = VendorPerformanceBuilder.PreviousWindow(from, to);
        var depotFilter = string.IsNullOrWhiteSpace(request.DepotCode) ? null : request.DepotCode.Trim();

        VendingOverviewModel overview;
        var sales = new List<RouteCustomerSalesRowModel>();
        var previousSales = new List<RouteCustomerSalesRowModel>();
        RouteCustomerProductSalesModel items;
        RouteCustomerProductSalesModel previousItems;

        try
        {
            overview = await vendingService.GetOverviewAsync(cancellationToken);

            var depots = overview.Depots
                .Where(depot => depotFilter is null
                    || string.Equals(depot.BusinessPartnerCode, depotFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (depots.Count == 0)
            {
                return Errors.Vending.ReportFailed("There is no depot to report on.");
            }

            // One depot at a time, as the page reads them: the calls share the circuit's HttpClient and
            // its token refresh, and there are only ever a handful of depots.
            foreach (var depot in depots)
            {
                var current = await routeCustomerService.GetSalesSummaryAsync(
                    depot.BusinessPartnerCode, from, to, includeInactive: true, scope: RouteCustomerScope.Vending);
                sales.AddRange(current.Routes.SelectMany(route => route.Customers));

                var previous = await routeCustomerService.GetSalesSummaryAsync(
                    depot.BusinessPartnerCode, previousFrom, previousTo, includeInactive: true, scope: RouteCustomerScope.Vending);
                previousSales.AddRange(previous.Routes.SelectMany(route => route.Customers));
            }

            items = await routeCustomerService.GetProductSalesAsync(depotFilter, from, to, RouteCustomerScope.Vending);
            previousItems = await routeCustomerService.GetProductSalesAsync(depotFilter, previousFrom, previousTo, RouteCustomerScope.Vending);
        }
        catch (InvalidOperationException ex)
        {
            return Errors.Vending.ReportFailed($"The vendor report could not be built: {ex.Message}");
        }

        var report = VendorPerformanceBuilder.Build(new VendorPerformanceSources(
            overview,
            await DepotNamesAsync(overview, cancellationToken),
            depotFilter,
            from,
            to,
            previousFrom,
            previousTo,
            sales,
            previousSales,
            items.Rows,
            previousItems.Rows));

        byte[] content;
        try
        {
            content = reportExport.ExportVendorPerformanceToExcel(report);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing the vendor performance workbook for {From:yyyy-MM-dd} to {To:yyyy-MM-dd} failed", from, to);
            return Errors.Vending.ReportFailed("The vendor report could not be written.");
        }

        var scope = depotFilter ?? "All_depots";
        return new ExportVendorPerformanceResult(
            $"Vendor_Performance_{scope}_{from:yyyyMMdd}-{to:yyyyMMdd}.xlsx",
            content);
    }

    /// <summary>Names only. A failed lookup leaves the codes showing, which is still correct.</summary>
    private async Task<Dictionary<string, string>> DepotNamesAsync(VendingOverviewModel overview, CancellationToken cancellationToken)
    {
        var codes = overview.Depots.Select(depot => depot.BusinessPartnerCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var partner in await masterDataCache.GetBusinessPartnersAsync(cancellationToken: cancellationToken))
            {
                if (partner.CardCode is not null && codes.Contains(partner.CardCode) && !string.IsNullOrWhiteSpace(partner.CardName))
                {
                    names.TryAdd(partner.CardCode, partner.CardName);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Depot names could not be read for the vendor report");
        }

        return names;
    }
}
