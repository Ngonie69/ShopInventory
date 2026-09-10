using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Builds the batch allocator the posting services take, for tests.
/// </summary>
/// <remarks>
/// A posting test is about what the pass does with the answer, not about FEFO ordering, so these
/// stand in for the real allocator and give one of the three answers it can give: the warehouse
/// holds the stock, the warehouse does not, or the warehouse could not be read. The last two are
/// told apart on purpose — one spends an attempt and one must not.
/// </remarks>
internal static class SaleBatchAllocators
{
    /// <summary>
    /// A warehouse holding one batch per item, with more than enough in it. Every batch-managed line
    /// is allocated wholly from <paramref name="batchNumber"/> plus the line's own number, so a test
    /// can see which line a selection landed on.
    /// </summary>
    public static IBatchInventoryValidationService Holding(string batchNumber = "BATCH")
        => StubProxy.For<IBatchInventoryValidationService>((method, args) => method.Name switch
        {
            nameof(IBatchInventoryValidationService.ValidateAndAllocateBatchesAsync) =>
                Task.FromResult(Allocate((CreateInvoiceRequest)args![0]!, batchNumber)),

            _ => throw new InvalidOperationException($"Unexpected allocator call: {method.Name}")
        });

    /// <summary>A warehouse whose stock SAP would not report, which is a retry rather than a refusal.</summary>
    public static IBatchInventoryValidationService Unreadable()
        => Refusing(
            BatchValidationErrorCode.StockUnknown,
            "Stock for item CHE011 in warehouse KEFGRS could not be read from SAP, so this document "
            + "cannot be checked against it. The reading is temporarily unavailable, not zero.");

    /// <summary>A warehouse that genuinely does not hold the stock, which is a person's problem.</summary>
    public static IBatchInventoryValidationService Short()
        => Refusing(
            BatchValidationErrorCode.InsufficientTotalStock,
            "Insufficient total stock. Need 3.0000, available 1.0000");

    private static IBatchInventoryValidationService Refusing(BatchValidationErrorCode code, string message)
        => StubProxy.For<IBatchInventoryValidationService>((method, _) => method.Name switch
        {
            nameof(IBatchInventoryValidationService.ValidateAndAllocateBatchesAsync) =>
                Task.FromResult(BatchAllocationResult.Failure(
                [
                    new BatchValidationErrorDto { ErrorCode = code, Message = message }
                ])),

            _ => throw new InvalidOperationException($"Unexpected allocator call: {method.Name}")
        });

    private static BatchAllocationResult Allocate(CreateInvoiceRequest request, string batchNumber)
    {
        var allocated = (request.Lines ?? [])
            .Select((line, index) => new AllocatedBatchLine
            {
                LineNumber = index + 1,
                ItemCode = line.ItemCode ?? "",
                WarehouseCode = line.WarehouseCode ?? "",
                IsBatchManaged = true,
                OriginalRequestedQuantity = line.Quantity,
                TotalQuantityAllocated = line.Quantity,
                UoMConversionFactor = 1m,
                Batches =
                [
                    new AllocatedBatch
                    {
                        BatchNumber = $"{batchNumber}{index + 1}",
                        QuantityAllocated = line.Quantity,
                        AllocationOrder = 1
                    }
                ]
            })
            .ToList();

        return BatchAllocationResult.Success(allocated, BatchAllocationStrategy.FEFO);
    }
}
