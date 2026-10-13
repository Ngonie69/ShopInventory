namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>What one item sold for across the period in one line of business, before VAT.</summary>
public sealed class DesktopSalesItemRow
{
    /// <summary>
    /// The line of business that sold it: "shops", "vending" or "vans". An item two of them sold is two
    /// rows, each ranked and shared within its own business.
    /// </summary>
    public string Business { get; set; } = string.Empty;

    public string ItemCode { get; set; } = string.Empty;

    public string? ItemDescription { get; set; }

    public decimal Quantity { get; set; }

    public decimal NetAmount { get; set; }

    /// <summary>How many sales included it, not how many lines.</summary>
    public int SalesCount { get; set; }

    /// <summary>Its share of the net value of every line its business sold.</summary>
    public decimal ShareOfNetPercent { get; set; }
}

/// <summary>What one line of business sold in a currency, of which its rows in <c>TopItems</c> are the head.</summary>
public sealed class DesktopSalesBusinessItems
{
    public string Business { get; set; } = string.Empty;

    /// <summary>What the page calls it: "Shops", "Vending", "Van sales".</summary>
    public string Label { get; set; } = string.Empty;

    public int DistinctItems { get; set; }

    public decimal QuantitySold { get; set; }

    /// <summary>The net value of every line it sold, which its items' shares are of.</summary>
    public decimal NetAmount { get; set; }
}
