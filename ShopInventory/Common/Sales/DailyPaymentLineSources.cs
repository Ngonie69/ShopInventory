namespace ShopInventory.Common.Sales;

/// <summary>
/// Where an invoice on a daily incoming payment came from, in the words the payment drawer and the
/// emailed workbook both use, so the two never name the same invoice differently.
/// </summary>
public static class DailyPaymentLineSources
{
    public const string ConsolidatedDay = "Consolidated day";

    /// <summary>A van sale made online: its invoice went straight to SAP through a stock reservation.</summary>
    public const string VanOnline = "Van (online)";

    /// <summary>The source of a till, vending or van sale held in <c>DesktopSales</c>.</summary>
    public static string OfSale(string? sourceSystem) => sourceSystem switch
    {
        SaleSourceSystems.ShopTill => "Shop till",
        SaleSourceSystems.Vending => "Vending",
        SaleSourceSystems.VanSales => "Van",
        _ => sourceSystem ?? "Sale"
    };
}
