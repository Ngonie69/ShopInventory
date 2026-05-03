using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Batches.Commands.UpdateBatchStatus;

public sealed class UpdateBatchStatusHandler(
    HttpClient httpClient,
    IAuditService auditService,
    ILogger<UpdateBatchStatusHandler> logger
) : IRequestHandler<UpdateBatchStatusCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UpdateBatchStatusCommand request,
        CancellationToken cancellationToken)
    {
        var normalizedStatus = NormalizeStatus(request.Status);

        try
        {
            var response = await httpClient.PatchAsJsonAsync(
                $"api/batch/{request.BatchEntryId}/status",
                new UpdateBatchStatusApiRequest { Status = normalizedStatus },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Failed to update batch {BatchEntryId} to {Status}. Status code: {StatusCode}",
                    request.BatchEntryId,
                    normalizedStatus,
                    (int)response.StatusCode);

                await TryAuditAsync(
                    auditService,
                    request,
                    false,
                    $"HTTP {(int)response.StatusCode}");

                return Errors.Batch.UpdateFailed("Failed to update batch status.");
            }

            await TryAuditAsync(auditService, request, true, null);
            return Result.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating batch status in web CQRS handler");
            await TryAuditAsync(auditService, request, false, ex.Message);
            return Errors.Batch.UpdateFailed("Failed to update batch status.");
        }
    }

    private static string NormalizeStatus(string status)
        => string.Equals(status, "Not Accessible", StringComparison.OrdinalIgnoreCase)
            ? "NotAccessible"
            : status;

    private static async Task TryAuditAsync(
        IAuditService auditService,
        UpdateBatchStatusCommand request,
        bool isSuccess,
        string? errorMessage)
    {
        try
        {
            await auditService.LogAsync(
                "BatchStatusUpdated",
                "Batch",
                request.BatchEntryId.ToString(),
                $"{(isSuccess ? "Updated" : "Failed to update")} batch {request.BatchNumber} ({request.ItemCode}) to {request.Status}",
                isSuccess,
                errorMessage);
        }
        catch
        {
        }
    }
}