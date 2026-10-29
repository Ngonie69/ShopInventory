namespace ShopInventory.Web.Models;

// The ADR report's order-to-sale figures, mirroring the API's AdrConversion*Result records by hand.
// Nullability matches the API field for field: a conversion is null where nothing was ordered, a
// median where nothing was invoiced, and a call count where nobody recorded a visit. Computed
// properties are re-derived because they mirror the API's own.

/// <summary>Every ADR order followed to the van sale it became, with the period before beside it.</summary>
public class AdrConversionReport
{
    public AdrConversionSummary Overall { get; set; } = new();
    public List<AdrConversionRow> Adrs { get; set; } = [];
    public List<AdrConversionDay> Days { get; set; } = [];
    public List<AdrConversionShop> Shops { get; set; } = [];
    public List<AdrConversionItem> Items { get; set; } = [];
    public List<AdrLedgerOrder> Orders { get; set; } = [];
    public AdrConversionComparison? Previous { get; set; }
}

public class AdrConversionComparison
{
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public AdrConversionSummary Overall { get; set; } = new();
    public List<AdrConversionRow> Adrs { get; set; } = [];
}

public class AdrConversionRow
{
    public Guid UserId { get; set; }
    public string? RouteCode { get; set; }
    public string? RouteName { get; set; }
    public AdrConversionSummary Summary { get; set; } = new();
}

public class AdrConversionSummary
{
    public AdrFunnelCounts Counts { get; set; } = new();
    public List<AdrConversionMoney> Money { get; set; } = [];
    public double? MedianDaysToInvoice { get; set; }
    public double? InvoicedByNextDay { get; set; }
    public int? Calls { get; set; }
    public int ProductiveCalls { get; set; }

    public double? StrikeRate => Calls is > 0 ? (double)ProductiveCalls / Calls.Value : null;

    /// <summary>The money in one currency, or null when nothing was ordered in it.</summary>
    public AdrConversionMoney? In(string? currency) =>
        currency is null
            ? null
            : Money.FirstOrDefault(money => string.Equals(money.Currency, currency, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Where a set of orders ended up; each order is in exactly one of the six outcomes.</summary>
public class AdrFunnelCounts
{
    public int Ordered { get; set; }
    public int ReachedSap { get; set; }
    public int Invoiced { get; set; }
    public int InvoicedInFull { get; set; }
    public int ShortSupplied { get; set; }
    public int Repriced { get; set; }
    public int NoLinkedInvoice { get; set; }
    public int NotYetInSap { get; set; }
    public int Cancelled { get; set; }
}

/// <summary>
/// One currency's orders and what they became. The five reasons add up to <see cref="Gap"/>; Repriced
/// is negative where the van sold more than was ordered.
/// </summary>
public class AdrConversionMoney
{
    public string Currency { get; set; } = string.Empty;
    public decimal Ordered { get; set; }
    public decimal ReachedSap { get; set; }
    public decimal Invoiced { get; set; }
    public decimal ShortSupplied { get; set; }
    public decimal Repriced { get; set; }
    public decimal NoLinkedInvoice { get; set; }
    public decimal NotYetInSap { get; set; }
    public decimal Cancelled { get; set; }
    public decimal OrderedOnInvoiced { get; set; }
    public decimal VanSales { get; set; }
    public decimal AdrLedVanSales { get; set; }
    public decimal? GrossProfit { get; set; }
    public decimal? CostedNetAmount { get; set; }

    public decimal Gap => Ordered - Invoiced;

    public double? Conversion => Ordered > 0 ? (double)(Invoiced / Ordered) : null;

    public double? FillRate => OrderedOnInvoiced > 0 ? 1 - (double)(ShortSupplied / OrderedOnInvoiced) : null;

    public double? VanShare => VanSales > 0 ? (double)(AdrLedVanSales / VanSales) : null;

    public decimal? MarginPercent => GrossProfit is { } profit && CostedNetAmount is { } costed && costed != 0
        ? decimal.Round(profit / costed * 100m, 1, MidpointRounding.AwayFromZero)
        : null;
}

public class AdrConversionDay
{
    public Guid UserId { get; set; }
    public DateTime TradingDate { get; set; }
    public int? Calls { get; set; }
    public int ProductiveCalls { get; set; }
    public int Orders { get; set; }
    public List<AdrConversionDayMoney> Money { get; set; } = [];
}

public class AdrConversionDayMoney
{
    public string Currency { get; set; } = string.Empty;
    public decimal Ordered { get; set; }
    public decimal Invoiced { get; set; }
    public decimal ShortSupplied { get; set; }

    public double? Conversion => Ordered > 0 ? (double)(Invoiced / Ordered) : null;
}

public class AdrConversionShop
{
    public Guid UserId { get; set; }
    public string CustomerCode { get; set; } = string.Empty;
    public string? CustomerName { get; set; }
    public int Visits { get; set; }
    public int Orders { get; set; }
    public int Invoiced { get; set; }
    public int ShortSupplied { get; set; }
    public int Uninvoiced { get; set; }
    public List<AdrConversionDayMoney> Money { get; set; } = [];
    public DateTime? LastOrderOn { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(CustomerName) ? CustomerCode : CustomerName;
}

public class AdrConversionItem
{
    public Guid UserId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string? UoMCode { get; set; }
    public int LineCount { get; set; }
    public int ShopCount { get; set; }
    public decimal OrderedQuantity { get; set; }
    public decimal SuppliedQuantity { get; set; }
    public decimal ShortQuantity { get; set; }
    public int ShortShopCount { get; set; }
    public List<AdrConversionItemMoney> Money { get; set; } = [];

    public double? FillRate => OrderedQuantity > 0 ? 1 - (double)(ShortQuantity / OrderedQuantity) : null;

    public string DisplayName => string.IsNullOrWhiteSpace(ItemDescription) ? ItemCode : ItemDescription;
}

public class AdrConversionItemMoney
{
    public string Currency { get; set; } = string.Empty;
    public decimal Ordered { get; set; }
    public decimal ShortSupplied { get; set; }
}

/// <summary>One order beside the invoice it became; the invoice columns are null until there is one.</summary>
public class AdrLedgerOrder
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public DateTime TradingDate { get; set; }
    public DateTime CapturedAtCat { get; set; }
    public string? CustomerCode { get; set; }
    public string? CustomerName { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int LineCount { get; set; }
    public decimal Ordered { get; set; }
    public int? SapOrderDocNum { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? InvoiceReference { get; set; }
    public int? InvoiceDocNum { get; set; }
    public DateTime? InvoicedAtCat { get; set; }
    public decimal? Invoiced { get; set; }
    public double? DaysToInvoice { get; set; }
    public decimal ShortSupplied { get; set; }
    public decimal Repriced { get; set; }

    public decimal? Variance => Invoiced is { } invoiced ? invoiced - Ordered : null;

    public string DisplayCustomer =>
        !string.IsNullOrWhiteSpace(CustomerName) ? CustomerName
        : !string.IsNullOrWhiteSpace(CustomerCode) ? CustomerCode
        : "No shop recorded";
}

/// <summary>The six outcomes, as the API names them.</summary>
public static class AdrOutcome
{
    public const string InvoicedInFull = "Invoiced in full";
    public const string ShortSupplied = "Short-supplied";
    public const string Repriced = "Repriced";
    public const string NoLinkedInvoice = "No linked invoice";
    public const string NotYetInSap = "Not yet in SAP";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// The page's and the workbook's shared reading of the conversion figures, so the two can never
/// describe the same period differently.
/// </summary>
public static class AdrConversionReading
{
    /// <summary>The currency most was ordered in: every ranking and tile leads on it.</summary>
    public static string? LeadCurrency(AdrConversionReport? conversion) =>
        conversion?.Overall.Money.FirstOrDefault()?.Currency;

    /// <summary>The reasons the gap is split into, in the order the page lists them, with each one's value.</summary>
    public static IReadOnlyList<(string Reason, string Explanation, int Orders, decimal Value)> GapReasons(
        AdrConversionSummary summary, AdrConversionMoney? money) =>
    [
        ("Short-supplied", "Invoiced with fewer units than ordered", summary.Counts.ShortSupplied, money?.ShortSupplied ?? 0m),
        ("No linked invoice", "In SAP or marked fulfilled, with no invoice traced here — often invoiced directly in SAP B1",
            summary.Counts.NoLinkedInvoice, money?.NoLinkedInvoice ?? 0m),
        ("Cancelled or rejected", "Withdrawn before it was invoiced", summary.Counts.Cancelled, money?.Cancelled ?? 0m),
        ("Not yet in SAP", "Still on the handset or waiting to post", summary.Counts.NotYetInSap, money?.NotYetInSap ?? 0m),
        ("Repriced at invoice", "Invoiced at a different price than ordered", summary.Counts.Repriced, money?.Repriced ?? 0m)
    ];

    /// <summary>Why a shop stands out, or null when it does not.</summary>
    public static string? ShopFlag(AdrConversionShop shop, string? lead)
    {
        if (shop.Orders == 0)
        {
            return shop.Visits > 0 ? "Visited, never ordered" : null;
        }

        if (shop.ShortSupplied >= 2)
        {
            return $"Short-supplied {shop.ShortSupplied}×";
        }

        if (shop.Uninvoiced >= 2)
        {
            return $"{shop.Uninvoiced} orders not invoiced";
        }

        var money = shop.Money.FirstOrDefault(part => string.Equals(part.Currency, lead, StringComparison.OrdinalIgnoreCase));
        return money?.Conversion is < 0.7 && shop.Orders >= 2 ? "Orders rarely arrive" : null;
    }
}
