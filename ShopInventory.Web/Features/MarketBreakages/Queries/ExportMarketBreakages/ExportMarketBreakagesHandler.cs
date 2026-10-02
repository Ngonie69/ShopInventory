using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.MarketBreakages.Queries.ExportMarketBreakages;

/// <remarks>
/// The workbook is built here from the API's export; the PDF is the API's, laid out there with the PDF
/// library the Web does not carry. Both read the same filter, so they hold the same reports.
/// </remarks>
public sealed class ExportMarketBreakagesHandler(
    IMarketBreakageService breakageService,
    IReportExportService reportExport,
    ILogger<ExportMarketBreakagesHandler> logger)
    : IRequestHandler<ExportMarketBreakagesQuery, ErrorOr<MarketBreakageExportFile>>
{
    private const string ExcelType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task<ErrorOr<MarketBreakageExportFile>> Handle(
        ExportMarketBreakagesQuery request,
        CancellationToken cancellationToken)
    {
        var stamp = IAuditService.ToCAT(DateTime.UtcNow).ToString("yyyyMMdd_HHmm");
        var name = $"Market_Breakages_{MarketBreakageStatus.ScopeLabel(request.Status).Replace(' ', '_')}_{stamp}";

        try
        {
            if (request.Format == MarketBreakageExportFormat.Pdf)
            {
                var (pdfOk, pdfMessage, pdf) = await breakageService.GetExportPdfAsync(request.Status, request.Search);
                return pdfOk && pdf is not null
                    ? new MarketBreakageExportFile(pdf, name + ".pdf", "application/pdf")
                    : Errors.MarketBreakage.ExportFailed(pdfMessage);
            }

            var (success, message, export) = await breakageService.GetExportAsync(request.Status, request.Search);
            if (!success || export is null)
                return Errors.MarketBreakage.ExportFailed(message);

            return new MarketBreakageExportFile(reportExport.ExportMarketBreakagesToExcel(export), name + ".xlsx", ExcelType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exporting market breakage reports as {Format} failed", request.Format);
            return Errors.MarketBreakage.ExportFailed("The breakage reports could not be exported.");
        }
    }
}
