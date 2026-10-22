namespace ShopInventory.Web.Features.Vending;

/// <summary>
/// Money kept apart by currency. A depot that took USD 40 and ZWG 900 took no single number, so nothing
/// here adds one currency to another; a figure that has to be one number — a share, a rank, a change —
/// is read in the report's main currency and says so.
/// </summary>
public sealed class CurrencyAmounts
{
    private readonly Dictionary<string, decimal> _amounts = new(StringComparer.OrdinalIgnoreCase);

    public decimal this[string currency] => _amounts.GetValueOrDefault(currency);

    public IReadOnlyCollection<string> Currencies => _amounts.Keys;

    public void Add(string currency, decimal amount) =>
        _amounts[currency] = _amounts.GetValueOrDefault(currency) + amount;

    /// <summary>Whether anything was added in <paramref name="currency"/>, which a zero cannot say.</summary>
    public bool Has(string currency) => _amounts.ContainsKey(currency);

    /// <summary>
    /// The margin <paramref name="profit"/> is of <paramref name="costed"/> in one currency, as a fraction;
    /// null where nothing in it was costed.
    /// </summary>
    public static decimal? Margin(CurrencyAmounts profit, CurrencyAmounts costed, string currency) =>
        profit.Has(currency) && costed[currency] != 0 ? profit[currency] / costed[currency] : null;

    public void Add(CurrencyAmounts other)
    {
        foreach (var currency in other.Currencies)
        {
            Add(currency, other[currency]);
        }
    }
}

/// <summary>Whether a vendor sold in the period, and if not, whether they ever have.</summary>
public enum VendorStanding
{
    Selling,

    /// <summary>Has sold before, but not in these dates.</summary>
    Quiet,

    NeverSold
}

/// <summary>
/// The Depots &amp; vendors workbook's figures: every depot, every vendor and every item over one period,
/// set against the period before it. Built by <see cref="VendorPerformanceBuilder"/>; laid out by the
/// export service.
/// </summary>
public sealed class VendorPerformanceReport
{
    public required DateTime From { get; init; }

    public required DateTime To { get; init; }

    public required DateTime PreviousFrom { get; init; }

    public required DateTime PreviousTo { get; init; }

    /// <summary>The currency shares, ranks and changes are read in: the one most invoices were written in.</summary>
    public required string Currency { get; init; }

    /// <summary>Every currency traded in either period, <see cref="Currency"/> first.</summary>
    public required IReadOnlyList<string> Currencies { get; init; }

    /// <summary>"All depots", or the one depot the page was filtered to.</summary>
    public required string ScopeLabel { get; init; }

    public required VendorPerformanceTotals Totals { get; init; }

    public required IReadOnlyList<DepotPerformance> Depots { get; init; }

    /// <summary>Every vendor in the report, best first.</summary>
    public required IReadOnlyList<VendorPerformance> Vendors { get; init; }

    /// <summary>Every item sold, best first.</summary>
    public required IReadOnlyList<ProductPerformance> Products { get; init; }

    /// <summary>Every vendor's purchases of every item, by depot and vendor.</summary>
    public required IReadOnlyList<VendorProductPerformance> VendorProducts { get; init; }
}

public sealed class VendorPerformanceTotals
{
    public CurrencyAmounts Gross { get; } = new();

    public CurrencyAmounts Net { get; } = new();

    public CurrencyAmounts PreviousGross { get; } = new();

    public int Invoices { get; init; }

    public int PreviousInvoices { get; init; }

    public int ActiveVendors { get; init; }

    public int SellingVendors { get; init; }

    public int ItemsSold { get; init; }

    /// <summary>Change in sales against the previous period, in the main currency. Null when it sold nothing then.</summary>
    public decimal? Change { get; init; }

    public decimal? AverageInvoice { get; init; }

    /// <summary>SAP's booked gross profit per currency, on the posted sales only; a currency with none is absent.</summary>
    public CurrencyAmounts GrossProfit { get; } = new();

    /// <summary>The value before VAT <see cref="GrossProfit"/> was measured on, per currency.</summary>
    public CurrencyAmounts CostedNet { get; } = new();
}

public sealed class DepotPerformance
{
    public required string Code { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyList<string> WarehouseCodes { get; init; }

    public required int Cashiers { get; init; }

    /// <summary>Vendors on the depot's books now.</summary>
    public required int ActiveVendors { get; init; }

    public required int SellingVendors { get; init; }

    public required int Invoices { get; init; }

    public required int PreviousInvoices { get; init; }

    public required int Lines { get; init; }

    public CurrencyAmounts Gross { get; } = new();

    public CurrencyAmounts Net { get; } = new();

    public CurrencyAmounts PreviousGross { get; } = new();

    /// <summary>Of all the depots' sales in the report, main currency.</summary>
    public decimal? Share { get; init; }

    public decimal? Change { get; init; }

    public decimal? AveragePerSellingVendor { get; init; }

    public decimal? AverageInvoice { get; init; }

    public required IReadOnlyList<VendorPerformance> Vendors { get; init; }

    /// <summary>The depot's own item ranking, its reach read against its own selling vendors.</summary>
    public required IReadOnlyList<ProductPerformance> Products { get; init; }

    /// <summary>SAP's booked gross profit per currency, on the posted sales only; a currency with none is absent.</summary>
    public CurrencyAmounts GrossProfit { get; } = new();

    /// <summary>The value before VAT <see cref="GrossProfit"/> was measured on, per currency.</summary>
    public CurrencyAmounts CostedNet { get; } = new();
}

public sealed class VendorPerformance
{
    public int? Id { get; init; }

    public required string Code { get; init; }

    public required string Name { get; init; }

    public string? Phone { get; init; }

    public required string DepotCode { get; init; }

    public required string DepotName { get; init; }

    /// <summary>False once the vendor has been removed. A removed vendor is listed only if they sold.</summary>
    public required bool IsActive { get; init; }

    public required VendorStanding Standing { get; init; }

    public required int Invoices { get; init; }

    public required int PreviousInvoices { get; init; }

    public required int Lines { get; init; }

    public CurrencyAmounts Gross { get; } = new();

    public CurrencyAmounts Net { get; } = new();

    public CurrencyAmounts PreviousGross { get; } = new();

    /// <summary>Across every depot in the report.</summary>
    public int Rank { get; set; }

    public int RankInDepot { get; set; }

    public decimal? ShareOfDepot { get; set; }

    public decimal? ShareOfAll { get; set; }

    public decimal? AverageInvoice { get; init; }

    public decimal? Change { get; init; }

    public int ItemCount { get; init; }

    public string? TopItemCode { get; init; }

    public string? TopItemDescription { get; init; }

    /// <summary>All time, not the period's — so a quiet vendor can be told from one who never sold.</summary>
    public DateTime? LastSaleAt { get; init; }

    public int? DaysSinceLastSale { get; init; }

    /// <summary>SAP's booked gross profit per currency, on the posted sales only; a currency with none is absent.</summary>
    public CurrencyAmounts GrossProfit { get; } = new();

    /// <summary>The value before VAT <see cref="GrossProfit"/> was measured on, per currency.</summary>
    public CurrencyAmounts CostedNet { get; } = new();
}

public sealed class ProductPerformance
{
    public required int Rank { get; init; }

    public required string ItemCode { get; init; }

    public string? Description { get; init; }

    public string? UoMCode { get; init; }

    /// <summary>Safe to add within one item, which is one unit of measure. Never summed across items.</summary>
    public required decimal Quantity { get; init; }

    /// <summary>The lines' totals, before VAT.</summary>
    public CurrencyAmounts Net { get; } = new();

    public CurrencyAmounts PreviousNet { get; } = new();

    public decimal? Share { get; init; }

    /// <summary>This item's share added to every better seller's. What the A/B/C class is cut on.</summary>
    public decimal? CumulativeShare { get; init; }

    /// <summary>A: the items that make the first 80% of sales. B: the next 15%. C: the last 5%.</summary>
    public required char Class { get; init; }

    public required int Lines { get; init; }

    public required int Vendors { get; init; }

    /// <summary>Of the vendors who sold anything in the period, the share who sold this.</summary>
    public decimal? Reach { get; init; }

    /// <summary>Net value over quantity, main currency.</summary>
    public decimal? AveragePrice { get; init; }

    public decimal? Change { get; init; }

    /// <summary>Main-currency net value at each depot, by depot code.</summary>
    public required IReadOnlyDictionary<string, decimal> NetByDepot { get; init; }

    public required DateTime LastSoldAt { get; init; }

    /// <summary>SAP's booked gross profit per currency, on the posted sales only; a currency with none is absent.</summary>
    public CurrencyAmounts GrossProfit { get; } = new();

    /// <summary>The value before VAT <see cref="GrossProfit"/> was measured on, per currency.</summary>
    public CurrencyAmounts CostedNet { get; } = new();
}

public sealed class VendorProductPerformance
{
    public required string DepotName { get; init; }

    public required string VendorCode { get; init; }

    public required string VendorName { get; init; }

    public required string ItemCode { get; init; }

    public string? Description { get; init; }

    public string? UoMCode { get; init; }

    public required string Currency { get; init; }

    public required decimal Quantity { get; init; }

    public required decimal Net { get; init; }

    /// <summary>Of the vendor's own item sales in the same currency.</summary>
    public decimal? ShareOfVendor { get; init; }

    public required int Lines { get; init; }

    public required DateTime LastSoldAt { get; init; }
}
