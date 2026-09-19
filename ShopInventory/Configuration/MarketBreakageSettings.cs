namespace ShopInventory.Configuration;

/// <summary>
/// Settings for market breakages: stock a van collected back from shops, confirmed at the office
/// into a transfer from the van to the returns warehouse.
/// </summary>
public sealed class MarketBreakageSettings
{
    public const string SectionName = "MarketBreakages";

    /// <summary>The SAP warehouse a confirmed breakage is transferred into.</summary>
    public string ReturnsWarehouseCode { get; set; } = "RETURNS";
}
