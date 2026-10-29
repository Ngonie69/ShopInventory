using ErrorOr;
using MediatR;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.RouteCustomers.Queries.ExportRouteSalesDetail;

/// <summary>
/// Reads what the detail workbook needs beyond the page — every customer's items, and the product mix if
/// the page has not loaded it — and lays the workbook out.
/// </summary>
/// <remarks>
/// The items are read for the summary's own dates rather than the date pickers', which may have been moved
/// since Run: a workbook whose item sheet covers other days than its summary would not add up.
/// </remarks>
public sealed class ExportRouteSalesDetailHandler(
    IRouteCustomerService routeCustomers,
    IReportExportService export)
    : IRequestHandler<ExportRouteSalesDetailQuery, ErrorOr<byte[]>>
{
    public async Task<ErrorOr<byte[]>> Handle(ExportRouteSalesDetailQuery request, CancellationToken cancellationToken)
    {
        var summary = request.Summary;
        var route = string.IsNullOrWhiteSpace(request.AssignedBusinessPartnerCode)
            ? null
            : request.AssignedBusinessPartnerCode.Trim();

        try
        {
            var mix = request.ProductMix
                ?? await routeCustomers.GetProductMixAsync(route, null, summary.From, summary.To);
            var sales = await routeCustomers.GetProductSalesAsync(route, summary.From, summary.To);

            // Laying out thousands of rows takes seconds of CPU. Off the circuit's own thread, so the page
            // stays responsive — and shows its spinner — while the file is built.
            return await Task.Run(
                () => export.ExportRouteSalesDetailToExcel(summary, mix, sales, request.RouteLabels, request.Currency),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error.Failure("RouteSalesDetail.ExportFailed", ex.Message);
        }
    }
}
