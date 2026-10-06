namespace ShopInventory.Services;

/// <summary>How a sale filed ahead of its SAP invoice prices its receipt lines.</summary>
public enum PreSapLinePricing
{
    /// <summary>
    /// The exact VAT-inclusive unit price, with the lines settled to the sale's total: what every
    /// receipt is filed with now.
    /// </summary>
    Exact = 0,

    /// <summary>
    /// The unit price rounded to the cent, as every receipt was filed until 6 October 2026. Kept only so
    /// a credit can rebuild the lines of a receipt filed that way.
    /// </summary>
    Cents = 1
}
