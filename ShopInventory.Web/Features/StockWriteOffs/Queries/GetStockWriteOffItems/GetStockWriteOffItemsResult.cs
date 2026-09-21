namespace ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOffItems;

/// <summary>
/// The catalogue the write-off picker draws from.
/// </summary>
/// <param name="Items">Every active item, ordered by code.</param>
/// <param name="SyncedAt">
/// When the catalogue was last refreshed from SAP, in UTC, or null when it never has been. The page
/// reads it to say why a list is empty: a catalogue that has never synced is a different problem
/// from one an item is simply missing from.
/// </param>
public sealed record GetStockWriteOffItemsResult(IReadOnlyList<StockWriteOffItem> Items, DateTime? SyncedAt);
