using System.Net.Http.Json;
using ClosedXML.Excel;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CreditNotes.Commands.DuplicateCancelledCreditNotes;

public sealed class DuplicateCancelledCreditNotesHandler(
    IHttpClientFactory httpClientFactory,
    IAuditService auditService,
    ILogger<DuplicateCancelledCreditNotesHandler> logger
) : IRequestHandler<DuplicateCancelledCreditNotesCommand, ErrorOr<DuplicateCancelledCreditNotesExportResult>>
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task<ErrorOr<DuplicateCancelledCreditNotesExportResult>> Handle(
        DuplicateCancelledCreditNotesCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient("ShopInventoryApiLongRunning");
            var docEntries = request.CreditNoteDocEntries.Distinct().ToList();
            var payload = new { CreditNoteDocEntries = docEntries, request.Reason };

            var response = await client.PostAsJsonAsync("api/creditnote/duplicate-cancelled", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Cancelled credit note duplication failed with status {StatusCode}: {Body}",
                    (int)response.StatusCode,
                    ApiErrorResponse.SanitizeForLog(body));

                return Errors.CreditNote.DuplicateFailed(
                    ApiErrorResponse.GetFriendlyMessage(response.StatusCode, body, "Failed to duplicate selected credit notes."));
            }

            var result = await response.Content.ReadFromJsonAsync<DuplicateCancelledCreditNotesResult>(cancellationToken: cancellationToken);
            if (result is null)
            {
                return Errors.CreditNote.DuplicateFailed("Failed to read the duplication result.");
            }

            await TryAuditAsync(result);

            var bytes = BuildWorkbook(result);
            var fileName = $"Duplicated_Credit_Notes_{IAuditService.ToCAT(DateTime.UtcNow):yyyyMMdd_HHmm}.xlsx";

            return new DuplicateCancelledCreditNotesExportResult(
                result,
                fileName,
                ExcelContentType,
                Convert.ToBase64String(bytes));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error duplicating selected cancelled credit notes");
            return Errors.CreditNote.DuplicateFailed(
                ApiErrorResponse.GetFriendlyMessage(ex, "Failed to duplicate selected credit notes."));
        }
    }

    private static byte[] BuildWorkbook(DuplicateCancelledCreditNotesResult result)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("New Credit Notes");

        worksheet.Cell(1, 1).Value = "Duplicated Credit Notes";
        worksheet.Range(1, 1, 1, 9).Merge();
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 16;

        worksheet.Cell(2, 1).Value = $"Generated: {IAuditService.ToCAT(DateTime.UtcNow):dd MMM yyyy HH:mm}";
        worksheet.Range(2, 1, 2, 9).Merge();

        var headers = new[]
        {
            "Original DocEntry",
            "Original DocNum",
            "Original Credit Note",
            "New DocEntry",
            "New DocNum",
            "New Credit Note Number",
            "Status",
            "Message",
            "Succeeded"
        };

        for (var column = 0; column < headers.Length; column++)
        {
            var cell = worksheet.Cell(4, column + 1);
            cell.Value = headers[column];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#e8eaf6");
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }

        var row = 5;
        foreach (var item in result.Results)
        {
            worksheet.Cell(row, 1).Value = item.OriginalSapDocEntry;
            worksheet.Cell(row, 2).Value = item.OriginalSapDocNum;
            worksheet.Cell(row, 3).Value = item.OriginalCreditNoteNumber;
            worksheet.Cell(row, 4).Value = item.NewSapDocEntry;
            worksheet.Cell(row, 5).Value = item.NewSapDocNum;
            worksheet.Cell(row, 6).Value = item.NewCreditNoteNumber;
            worksheet.Cell(row, 7).Value = item.Status;
            worksheet.Cell(row, 8).Value = item.Message;
            worksheet.Cell(row, 9).Value = item.Success ? "Yes" : "No";

            var rowRange = worksheet.Range(row, 1, row, 9);
            rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            if (!item.Success)
            {
                rowRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#fee2e2");
            }

            row++;
        }

        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private async Task TryAuditAsync(DuplicateCancelledCreditNotesResult result)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.DuplicateCancelledCreditNotes,
                "CreditNote",
                null,
                $"Duplicated cancelled credit notes. Success: {result.SuccessCount}, Failed: {result.FailedCount}",
                result.FailedCount == 0,
                result.FailedCount == 0 ? null : "Some credit notes could not be duplicated.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit cancelled credit note duplication");
        }
    }
}