using System.Text.Json.Serialization;

namespace ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerCatalogue;

/// <summary>
/// How much of an item there is, as a customer is allowed to know it.
/// </summary>
/// <remarks>
/// A band rather than a quantity, deliberately and on two grounds. A depot figure is not a promise:
/// the van is loaded from it the afternoon before, alongside every other shop's order, so a number
/// shown here would be read as availability it cannot guarantee. And the quantity a supplier holds
/// is commercially sensitive — it says what they can and cannot fulfil this week, which is not a
/// customer's business and certainly not a competitor's.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VanSalesStockBand
{
    /// <summary>No stock projection covers this warehouse, so nothing truthful can be said.</summary>
    Unknown = 0,

    InStock = 1,

    /// <summary>At or below the configured threshold. Order early rather than do not order.</summary>
    Low = 2,

    /// <summary>
    /// Nothing on hand. Shown as a warning, never as a block — the decision is auto-accept with the
    /// rep adjusting at delivery, and a depot that restocks overnight would otherwise have refused
    /// an order it could have filled.
    /// </summary>
    OutOfStock = 3
}

/// <summary>One line of the customer's catalogue.</summary>
/// <remarks>
/// Both a net and a gross price are sent. The shopkeeper thinks in the price they will pay, the
/// order maths is done on the net, and having the handset derive one from the other is how the
/// two ends come to disagree about a total by a cent and turn a delivery into an argument.
/// </remarks>
public sealed record VanSalesCatalogueItem(
    string ItemCode,
    string ItemName,
    string? BarCode,
    string? UnitOfMeasure,
    string? Category,
    decimal UnitPrice,
    decimal TaxPercent,
    decimal UnitPriceIncludingTax,
    VanSalesStockBand Availability);

/// <summary>
/// The catalogue as the app caches it.
/// </summary>
/// <remarks>
/// <c>ETag</c> is a hash of the content. The app sends it back as <c>If-None-Match</c> and gets a
/// 304 when nothing has moved, which matters on a handset paying for its own data on a bad line.
/// <para>
/// There is deliberately no <c>since</c> parameter returning only what changed. Availability is not
/// stamped on any row — it comes from a stock projection that moves on every transfer and every
/// sale — so a delta filtered by row timestamps would quietly serve yesterday's stock bands while
/// looking complete. A whole catalogue that is either fresh or a 304 cannot do that.
/// </para>
/// </remarks>
public sealed record VanSalesCatalogueResult(
    int PriceListNumber,
    string? Currency,
    string? StockWarehouseCode,
    DateTime GeneratedAtUtc,
    string ETag,
    IReadOnlyList<VanSalesCatalogueItem> Items);
