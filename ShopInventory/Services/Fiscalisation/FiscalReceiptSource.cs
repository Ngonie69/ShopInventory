using ShopInventory.Common.Sales;

namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// Where a receipt was raised, stated to the fiscalisation platform so its Receipts drawer can say so. Sent as
/// <c>sourceChannel</c> / <c>sourceLocation</c> beside the receipt; the platform stores them on its archive row,
/// never sends them to FDMS and never signs them. A platform that predates the fields ignores them.
/// </summary>
/// <param name="Channel">How the sale was made, e.g. "Shop till" or "Van".</param>
/// <param name="Location">The warehouse it was sold from, e.g. "KEFGRS" or "VAN009".</param>
public sealed record FiscalReceiptSource(string Channel, string? Location)
{
    public const string SalesInvoice = "Sales invoice";
    public const string SalesCreditNote = "Credit note";
    public const string DesktopCreditNote = "Desktop credit note";

    /// <summary>The channel a <c>DesktopSaleEntity</c> or invoice-queue entry was raised in, from its <c>SourceSystem</c>.</summary>
    public static FiscalReceiptSource ForSale(string? sourceSystem, string? warehouseCode) =>
        new(ChannelFor(sourceSystem), LocationFor(warehouseCode));

    /// <summary>A document already in SAP, placed by the warehouses its lines are drawn from.</summary>
    public static FiscalReceiptSource ForSapDocument(string channel, IEnumerable<string?> lineWarehouses)
    {
        var warehouses = lineWarehouses
            .Select(LocationFor)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new(channel, warehouses.Count == 0 ? null : string.Join(", ", warehouses));
    }

    public static string ChannelFor(string? sourceSystem)
    {
        var source = sourceSystem?.Trim();
        if (SaleSourceSystems.IsVanSale(source)) return "Van";
        if (Is(source, SaleSourceSystems.ShopTill)) return "Shop till";
        if (Is(source, SaleSourceSystems.Vending)) return "Vending";
        if (Is(source, SaleSourceSystems.LegacyDesktop)) return "Desktop app";
        return string.IsNullOrEmpty(source) ? "ShopInventory" : source;
    }

    /// <summary>Applies this source to a request bound for the platform.</summary>
    public void ApplyTo(SubmitReceiptApiRequest request)
    {
        request.SourceChannel = Channel;
        request.SourceLocation = Location;
    }

    public static string? LocationFor(string? warehouseCode) =>
        string.IsNullOrWhiteSpace(warehouseCode) ? null : warehouseCode.Trim().ToUpperInvariant();

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
