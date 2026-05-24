using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CreditNotes.Commands.BulkCancelCreditNotes;

public sealed class BulkCancelCreditNotesHandler(
    IHttpClientFactory httpClientFactory,
    IAuditService auditService,
    ILogger<BulkCancelCreditNotesHandler> logger
) : IRequestHandler<BulkCancelCreditNotesCommand, ErrorOr<BulkCancelCreditNotesResult>>
{
    public async Task<ErrorOr<BulkCancelCreditNotesResult>> Handle(
        BulkCancelCreditNotesCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient("ShopInventoryApiLongRunning");
            var docEntries = request.CreditNoteDocEntries.Distinct().ToList();
            var payload = new { CreditNoteDocEntries = docEntries, request.Reason };

            var response = await client.PostAsJsonAsync("api/creditnote/bulk-cancel", payload, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Bulk credit note cancellation failed with status {StatusCode}: {Body}",
                    (int)response.StatusCode,
                    ApiErrorResponse.SanitizeForLog(body));

                return Errors.CreditNote.BulkCancelFailed(
                    ApiErrorResponse.GetFriendlyMessage(response.StatusCode, body, "Failed to cancel selected credit notes."));
            }

            var result = await response.Content.ReadFromJsonAsync<BulkCancelCreditNotesResult>(cancellationToken: cancellationToken);
            if (result is null)
            {
                return Errors.CreditNote.BulkCancelFailed("Failed to read the cancellation result.");
            }

            await TryAuditAsync(result);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cancelling selected credit notes");
            return Errors.CreditNote.BulkCancelFailed(
                ApiErrorResponse.GetFriendlyMessage(ex, "Failed to cancel selected credit notes."));
        }
    }

    private async Task TryAuditAsync(BulkCancelCreditNotesResult result)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.BulkCancelCreditNotes,
                "CreditNote",
                null,
                $"Bulk cancel completed. Success: {result.SuccessCount}, Failed: {result.FailedCount}",
                result.FailedCount == 0,
                result.FailedCount == 0 ? null : "Some credit notes could not be cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit bulk credit note cancellation");
        }
    }
}