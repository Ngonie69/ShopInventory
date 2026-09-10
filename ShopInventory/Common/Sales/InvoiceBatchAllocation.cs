using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Chooses the batches and serial numbers an invoice will issue from, and writes them onto the
/// request that is about to be posted.
///
/// SAP refuses a document whose batch-managed line names no batch — "Cannot add row without complete
/// selection of batch/serial numbers" — and it refuses the whole document, not the line. Nothing this
/// system captures can supply the selection: neither a till nor a van handset chooses a batch, both
/// sell by item. So every path that posts an A/R invoice has to allocate one here first, and the
/// three that do so had two copies of the same routine between them.
/// </summary>
public static class InvoiceBatchAllocation
{
    /// <summary>
    /// Allocates against the warehouse and, if that succeeded, writes the selection onto
    /// <paramref name="request"/>. The result is returned either way so the caller can decide what a
    /// failure means for its own document — the wording differs between a queue that will retry and
    /// an operator waiting on an answer.
    /// </summary>
    public static async Task<BatchAllocationResult> AllocateAsync(
        IBatchInventoryValidationService batchValidation,
        CreateInvoiceRequest request,
        BatchAllocationStrategy strategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default)
    {
        var result = await batchValidation.ValidateAndAllocateBatchesAsync(
            request, autoAllocate: true, strategy, cancellationToken);

        if (result.IsValid)
        {
            ApplyTo(request, result.AllocatedLines);
        }

        return result;
    }

    /// <summary>
    /// Copies an allocation onto the request's lines. A line that already names its own batches or
    /// serials keeps them: an explicit selection has been validated against stock and is the
    /// caller's, not this method's, to overwrite.
    /// </summary>
    public static void ApplyTo(CreateInvoiceRequest request, IReadOnlyList<AllocatedBatchLine> allocatedLines)
    {
        if (request.Lines == null)
            return;

        foreach (var allocatedLine in allocatedLines)
        {
            var lineIndex = allocatedLine.LineNumber - 1;
            if (lineIndex < 0 || lineIndex >= request.Lines.Count)
                continue;

            var requestLine = request.Lines[lineIndex];

            if (requestLine.BatchNumbers is not { Count: > 0 } && allocatedLine.Batches.Count > 0)
            {
                requestLine.BatchNumbers = allocatedLine.Batches
                    .Select(batch => new BatchNumberRequest
                    {
                        BatchNumber = batch.BatchNumber,
                        Quantity = batch.QuantityAllocated,
                        ExpiryDate = batch.ExpiryDate
                    })
                    .ToList();
            }

            if (requestLine.SerialNumbers is not { Count: > 0 } && allocatedLine.Serials.Count > 0)
            {
                requestLine.SerialNumbers = allocatedLine.Serials
                    .Select(serial => new SerialNumberRequest
                    {
                        InternalSerialNumber = serial.InternalSerialNumber,
                        SystemSerialNumber = serial.SystemSerialNumber
                    })
                    .ToList();
            }
        }
    }

    /// <summary>
    /// What to tell the operator, and what the failure classifiers read.
    /// </summary>
    /// <remarks>
    /// The two halves are worded apart on purpose. <c>SapFailureClassifier</c> decides from this
    /// message whether the document goes back on the queue or in front of a person, and an unread
    /// warehouse is the former — the stock position is unknown, not zero, and the next pass will
    /// very likely read it. Saying "insufficient" there would park a network blip as though the
    /// shop had sold out.
    /// </remarks>
    public static string DescribeFailure(BatchAllocationResult result, string document)
    {
        var unreadable = result.ValidationErrors
            .Any(error => error.ErrorCode == BatchValidationErrorCode.StockUnknown);

        return $"Batch allocation for {document} "
            + (unreadable ? "could not be completed" : "failed")
            + ": "
            + string.Join("; ", result.ValidationErrors.Select(error => error.Message));
    }
}
