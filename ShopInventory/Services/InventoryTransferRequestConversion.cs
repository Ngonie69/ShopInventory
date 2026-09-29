using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// The inventory transfer an inventory transfer request becomes.
/// </summary>
/// <remarks>
/// One definition, used both by <see cref="SAPServiceLayerClient.ConvertTransferRequestToTransferAsync"/>
/// to post and by <c>ConvertTransferRequestHandler</c> to check the depot's stock beforehand, so the
/// document that was checked is the document that posts.
/// </remarks>
public static class InventoryTransferRequestConversion
{
    public static CreateInventoryTransferRequest ToTransfer(InventoryTransferRequest transferRequest)
    {
        // Allocation is deliberately not done here: CreateInventoryTransferAsync resolves the
        // management flags and allocates every line of the document it is about to post, so a second
        // implementation here could only disagree with it — and did, by allocating each line from the
        // full batch pool regardless of what the other lines had already claimed.
        var lines = transferRequest.StockTransferLines?
            .Select(requestLine => new CreateInventoryTransferLineRequest
            {
                ItemCode = requestLine.ItemCode,
                Quantity = requestLine.Quantity,
                FromWarehouseCode = requestLine.FromWarehouseCode ?? transferRequest.FromWarehouse ?? "01",
                ToWarehouseCode = requestLine.WarehouseCode ?? transferRequest.ToWarehouse
            })
            .ToList() ?? [];

        return new CreateInventoryTransferRequest
        {
            FromWarehouse = transferRequest.FromWarehouse,
            ToWarehouse = transferRequest.ToWarehouse,
            DocDate = DateTime.Today.ToString("yyyy-MM-dd"),
            DueDate = transferRequest.DueDate,
            Comments = $"Converted from Transfer Request #{transferRequest.DocNum}. {transferRequest.Comments}".Trim(),
            Lines = lines
        };
    }
}
