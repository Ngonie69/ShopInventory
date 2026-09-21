namespace ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOffItems;

/// <summary>
/// One item the write-off picker can offer.
/// </summary>
/// <param name="ItemCode">SAP's item code — what the line carries and what the picker searches.</param>
/// <param name="ItemName">The description, as last synced from SAP. Null when SAP holds none.</param>
/// <param name="ManagesBatches">
/// True when SAP tracks this item by batch, which is what makes the batch picker appear: a
/// batch-managed line naming no batch fails the whole goods issue.
/// </param>
public sealed record StockWriteOffItem(string ItemCode, string? ItemName, bool ManagesBatches);
