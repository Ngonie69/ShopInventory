using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Vending.Queries.ExportVendingReconciliation;

/// <summary>
/// Reads the range again rather than exporting the rows on the page, so the workbook holds the counts
/// as they stand when it is asked for — an approval made in another tab is in it.
/// </summary>
public sealed class ExportVendingReconciliationHandler(
    IVendingService vendingService,
    IReportExportService reportExport,
    ILogger<ExportVendingReconciliationHandler> logger
) : IRequestHandler<ExportVendingReconciliationQuery, ErrorOr<ExportVendingReconciliationResult>>
{
    public async Task<ErrorOr<ExportVendingReconciliationResult>> Handle(
        ExportVendingReconciliationQuery request,
        CancellationToken cancellationToken)
    {
        var from = request.From.Date;
        var to = request.To.Date < from ? from : request.To.Date;

        var (reconciliation, error) = await vendingService.GetReconciliationAsync(from, to, request.WarehouseCode, cancellationToken);
        if (reconciliation is null)
        {
            return Errors.Vending.ReportFailed($"The reconciliation could not be read: {error}");
        }

        byte[] content;
        try
        {
            content = await Task.Run(() => reportExport.ExportVendingReconciliationToExcel(reconciliation, request.ScopeLabel), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Writing the vending reconciliation workbook for {From:yyyy-MM-dd} to {To:yyyy-MM-dd} failed", from, to);
            return Errors.Vending.ReportFailed("The reconciliation workbook could not be written.");
        }

        var scope = string.IsNullOrWhiteSpace(request.WarehouseCode) ? "All_depots" : request.WarehouseCode.Trim();
        return new ExportVendingReconciliationResult(
            $"Vending_Reconciliation_{scope}_{from:yyyyMMdd}-{to:yyyyMMdd}.xlsx",
            content);
    }
}
