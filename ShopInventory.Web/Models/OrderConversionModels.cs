namespace ShopInventory.Web.Models;

// The order conversion report, mirroring the API's OrderConversion*Result records by hand. The
// summaries, shop money and ledger orders are the ADR report's own classes, because the API reuses its
// records. Nullability matches the API field for field; computed properties are re-derived.

/// <summary>Van sales reps' and ADRs' orders followed to the sales they became.</summary>
public class OrderConversionReportResponse
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public string? LeadCurrency { get; set; }
    public AdrConversionSummary Overall { get; set; } = new();
    public List<OrderConversionRaiser> Raisers { get; set; } = [];
    public List<OrderConversionRep> Reps { get; set; } = [];
    public List<OrderConversionSalesMix> SalesMix { get; set; } = [];
    public OrderConversionProducts Products { get; set; } = new();
    public List<OrderConversionDay> Days { get; set; } = [];
    public List<OrderConversionShop> Shops { get; set; } = [];
    public List<OrderConversionLedgerRow> Orders { get; set; } = [];
    public int OrdersNotListed { get; set; }
    public List<OrderConversionProjection> Projections { get; set; } = [];
    public List<OrderConversionAttention> Attention { get; set; } = [];
    public OrderConversionComparison? Previous { get; set; }
    public List<string> Caveats { get; set; } = [];
}

/// <summary>The two kinds of raiser, as the API names them.</summary>
public static class OrderRaiser
{
    public const string SalesReps = "Sales";
    public const string Adrs = "ADR";

    public static string Label(string? role) => role == Adrs ? "ADRs" : "Sales reps";

    public static string Singular(string? role) => role == Adrs ? "ADR" : "Sales rep";
}

public class OrderConversionRaiser
{
    public string Role { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int People { get; set; }
    public int ActivePeople { get; set; }
    public AdrConversionSummary Summary { get; set; } = new();
}

public class OrderConversionRep
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string Role { get; set; } = string.Empty;
    public string? VanAccountCode { get; set; }
    public string? RouteCode { get; set; }
    public string? RouteName { get; set; }
    public bool IsActive { get; set; }
    public AdrConversionSummary Summary { get; set; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;
}

/// <summary>One currency of every van sale in the period, split by where it came from.</summary>
public class OrderConversionSalesMix
{
    public string Currency { get; set; } = string.Empty;
    public int SaleCount { get; set; }
    public decimal VanSales { get; set; }
    public int FromOrdersCount { get; set; }
    public decimal FromSalesRepOrders { get; set; }
    public decimal FromAdrOrders { get; set; }
    public decimal FromOtherOrders { get; set; }

    public decimal FromOrders => FromSalesRepOrders + FromAdrOrders + FromOtherOrders;

    public decimal Direct => VanSales - FromOrders;

    public double? FromOrdersShare => VanSales > 0 ? (double)(FromOrders / VanSales) : null;
}

public class OrderConversionProducts
{
    public int ProductsOrdered { get; set; }
    public int ProductsSupplied { get; set; }
    public int ProductsShort { get; set; }
    public int LinesOrdered { get; set; }
    public decimal UnitsOrdered { get; set; }
    public decimal UnitsOnInvoicedOrders { get; set; }
    public decimal UnitsSupplied { get; set; }
    public decimal UnitsNotInvoiced { get; set; }
    public List<OrderConversionItem> Items { get; set; } = [];

    public double? UnitConversion => UnitsOrdered > 0 ? Math.Min(1d, (double)(UnitsSupplied / UnitsOrdered)) : null;
}

public class OrderConversionItem
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string? UoMCode { get; set; }
    public int Orders { get; set; }
    public int Shops { get; set; }
    public decimal OrderedQuantity { get; set; }
    public decimal SuppliedQuantity { get; set; }
    public decimal ShortQuantity { get; set; }
    public decimal NotInvoicedQuantity { get; set; }
    public decimal SalesRepQuantity { get; set; }
    public decimal AdrQuantity { get; set; }
    public List<OrderConversionItemMoney> Money { get; set; } = [];

    public double? UnitConversion => OrderedQuantity > 0 ? Math.Min(1d, (double)(SuppliedQuantity / OrderedQuantity)) : null;

    public string DisplayName => string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;

    public OrderConversionItemMoney? In(string? currency) =>
        Money.FirstOrDefault(money => string.Equals(money.Currency, currency, StringComparison.OrdinalIgnoreCase));
}

public class OrderConversionItemMoney
{
    public string Currency { get; set; } = string.Empty;
    public decimal Ordered { get; set; }
    public decimal Converted { get; set; }
    public decimal ShortSupplied { get; set; }

    public double? Conversion => Ordered > 0 ? (double)(Converted / Ordered) : null;
}

public class OrderConversionDay
{
    public DateTime TradingDate { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int Orders { get; set; }
    public decimal SalesRepOrdered { get; set; }
    public decimal SalesRepInvoiced { get; set; }
    public decimal AdrOrdered { get; set; }
    public decimal AdrInvoiced { get; set; }

    public decimal Ordered => SalesRepOrdered + AdrOrdered;

    public decimal Invoiced => SalesRepInvoiced + AdrInvoiced;
}

public class OrderConversionShop
{
    public string CustomerCode { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public string RaisedBy { get; set; } = string.Empty;
    public int Orders { get; set; }
    public int Invoiced { get; set; }
    public int ShortSupplied { get; set; }
    public int Uninvoiced { get; set; }
    public List<AdrConversionDayMoney> Money { get; set; } = [];
    public DateTime? LastOrderOn { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(CustomerName) ? CustomerCode : CustomerName;

    public AdrConversionDayMoney? In(string? currency) =>
        Money.FirstOrDefault(money => string.Equals(money.Currency, currency, StringComparison.OrdinalIgnoreCase));
}

public class OrderConversionLedgerRow
{
    public AdrLedgerOrder Order { get; set; } = new();
    public string Role { get; set; } = string.Empty;
}

public class OrderConversionProjection
{
    public string Currency { get; set; } = string.Empty;
    public int OrderDays { get; set; }
    public decimal OrderedPerDay { get; set; }
    public decimal ConvertedPerDay { get; set; }
    public double? SettledConversion { get; set; }
    public decimal OpenOrdersValue { get; set; }
    public decimal ExpectedFromOpenOrders { get; set; }
    public double? ProjectedPeriodConversion { get; set; }
    public string HorizonLabel { get; set; } = string.Empty;
    public DateTime HorizonFrom { get; set; }
    public DateTime HorizonTo { get; set; }
    public int HorizonDays { get; set; }
    public decimal ProjectedOrdered { get; set; }
    public decimal ProjectedConverted { get; set; }
    public decimal? ProjectedMonthOrdered { get; set; }
    public decimal? ProjectedMonthConverted { get; set; }
    public double TargetConversion { get; set; }
    public decimal UpsideAtTarget { get; set; }
    public double? WeeklyTrendPoints { get; set; }
    public List<OrderConversionWeek> Weeks { get; set; } = [];

    public decimal ProjectedLeakage => ProjectedOrdered - ProjectedConverted;
}

public class OrderConversionWeek
{
    public DateTime WeekStart { get; set; }
    public int Orders { get; set; }
    public decimal Ordered { get; set; }
    public decimal Invoiced { get; set; }

    public double? Conversion => Ordered > 0 ? (double)(Invoiced / Ordered) : null;
}

/// <summary>Something to look at. Severity is "high", "medium" or "low".</summary>
public class OrderConversionAttention
{
    public string Severity { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string? Currency { get; set; }
    public decimal? ValueAtStake { get; set; }
    public string? Focus { get; set; }
}

public class OrderConversionComparison
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public AdrConversionSummary Overall { get; set; } = new();
    public List<OrderConversionRaiser> Raisers { get; set; } = [];
}
