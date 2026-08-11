namespace ShopInventory.Models;

/// <summary>
/// One cursor-paged window of the items a warehouse currently holds stock of.
/// </summary>
/// <param name="Items">The items this page resolved, in item code order.</param>
/// <param name="HasMore">Whether any codes remain past this page.</param>
/// <param name="NextCursor">
/// The last item code this page consumed, which is what the caller passes back to get the next one.
/// This is the last code <em>consumed</em> rather than the last item <em>returned</em>: a code that
/// resolved to nothing has still been dealt with, and naming it here stops the next page re-reading
/// it forever. Null once the warehouse is exhausted.
/// </param>
public sealed record WarehouseItemPage(List<Item> Items, bool HasMore, string? NextCursor);
