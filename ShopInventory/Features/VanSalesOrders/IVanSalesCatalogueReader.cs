namespace ShopInventory.Features.VanSalesOrders;

/// <summary>One sellable item, priced for the customer ordering app.</summary>
public sealed record VanSalesPricedItem(
    string ItemCode,
    string ItemName,
    string? BarCode,
    string? UnitOfMeasure,
    string? Category,
    decimal UnitPrice,
    decimal TaxPercent);

/// <summary>Everything a van sales customer may order, and what it costs.</summary>
public sealed record VanSalesPricedCatalogue(
    int PriceListNumber,
    string? Currency,
    IReadOnlyDictionary<string, VanSalesPricedItem> ItemsByCode);

/// <summary>
/// The single answer to "what may this customer order, and at what price?".
/// </summary>
/// <remarks>
/// Shared by the catalogue the app browses and the handler that accepts orders, deliberately. If
/// the two worked it out separately they would drift — a filter tightened on one side, a price
/// resolved differently on the other — and the failure would be an item shown at one price and
/// billed at another, or an order refused for containing something the app had just offered. The
/// shopkeeper would be right and the system would be wrong, at the door, in front of the rep.
/// </remarks>
public interface IVanSalesCatalogueReader
{
    Task<VanSalesPricedCatalogue> ReadAsync(CancellationToken cancellationToken);
}
