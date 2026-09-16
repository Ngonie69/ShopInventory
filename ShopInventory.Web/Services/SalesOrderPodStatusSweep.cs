using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Asks the API for the POD status of a list of sales orders, one batch at a time, and stops at the
/// first batch SAP could not answer.
/// </summary>
/// <remarks>
/// Mobile Orders checks every loaded order, not just the rows on screen, because its Delivered tab and
/// status sort need all of them. On 2026-09-16 one visit sent 36 batches back to back while SAP's
/// database was unreachable, and kept going for six minutes, each request failing after its own
/// retries. A failed batch now ends the sweep: the next batch runs the same query
/// against the same SAP and can only fail the same way.
/// </remarks>
public static class SalesOrderPodStatusSweep
{
    public const int BatchSize = 100;

    /// <param name="salesOrderDocNums">The SAP doc numbers to check, in the order to ask about them.</param>
    /// <param name="validateBatch">Asks the API about one batch; null means the request itself failed.</param>
    /// <param name="onBatchResults">Takes a batch's results and returns false to stop the sweep.</param>
    /// <param name="cancellationToken">Cancelled when the page reloads its orders or is left.</param>
    /// <returns>True when every batch was asked about and answered.</returns>
    public static async Task<bool> RunAsync(
        IEnumerable<int> salesOrderDocNums,
        Func<IReadOnlyList<int>, CancellationToken, Task<BulkPodValidationResponse?>> validateBatch,
        Func<IReadOnlyList<BulkPodValidationResult>, Task<bool>> onBatchResults,
        CancellationToken cancellationToken)
    {
        foreach (var batch in salesOrderDocNums.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await validateBatch(batch, cancellationToken);
            if (response == null)
                return false;

            // The orders SAP did answer for are still worth showing before stopping.
            if (!await onBatchResults(response.Results))
                return false;

            if (response.Results.Any(result => result.LookupFailed))
                return false;
        }

        return true;
    }
}
