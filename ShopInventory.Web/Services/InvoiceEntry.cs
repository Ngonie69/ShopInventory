using ShopInventory.Web.Components;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>Where a line's quantity stands against the stock in its warehouse.</summary>
public enum LineStockState
{
    /// <summary>No figure: not asked yet, the read failed, or the warehouse holds no row for the item.</summary>
    Unknown,

    /// <summary>A read is in flight.</summary>
    Loading,

    /// <summary>The warehouse holds enough.</summary>
    Enough,

    /// <summary>Enough, but the line takes most of what is there.</summary>
    Low,

    /// <summary>The warehouse holds less than the line asks for.</summary>
    Short
}

/// <param name="State">See <see cref="LineStockState"/>.</param>
/// <param name="InStock">SAP's in-stock figure for the line's warehouse, when there is one.</param>
/// <param name="ShortBy">How much the line asks for beyond what is in stock; 0 unless Short.</param>
public readonly record struct LineStock(LineStockState State, decimal? InStock, decimal ShortBy)
{
    public bool IsShort => State == LineStockState.Short;
}

/// <summary>
/// The rules behind /invoices/create that are worth testing without rendering the page:
/// when a line is short, which currency a customer fixes, the order of the customer list, and
/// what a line is worth.
/// </summary>
public static class InvoiceEntry
{
    /// <summary>SAP's "all currencies" marker on a business partner. Not a currency.</summary>
    public const string AnyCurrency = "##";

    /// <summary>How many recent customers the picker leads with.</summary>
    public const int RecentCustomerLimit = 5;

    /// <summary>A line taking more than this share of what is in stock is drawn as running low.</summary>
    private const decimal LowShare = 0.8m;

    /// <summary>
    /// Compares what the invoice asks of an item in a warehouse — every line for it, added up — with that warehouse's in-stock figure — OnHand, the same figure the API's
    /// posting check uses. A missing figure is never read as zero: a failed read and an item the
    /// warehouse has no row for look the same from here, and blocking an invoice on a guess would
    /// turn away a sale SAP would have taken. The API checks again when the invoice posts.
    /// </summary>
    public static LineStock EvaluateStock(decimal requested, decimal? inStock, bool loading)
    {
        if (inStock is not { } stock)
        {
            return new LineStock(loading ? LineStockState.Loading : LineStockState.Unknown, null, 0m);
        }

        if (requested <= 0)
        {
            // A line with no quantity is covered by its own "must be above zero" error.
            return new LineStock(LineStockState.Enough, stock, 0m);
        }

        if (requested > stock)
        {
            // Oversold stock reads negative; what the line is short by is all of it.
            return new LineStock(LineStockState.Short, stock, requested - Math.Max(stock, 0m));
        }

        return new LineStock(requested > stock * LowShare ? LineStockState.Low : LineStockState.Enough, stock, 0m);
    }

    /// <summary>
    /// The currency a customer fixes the invoice to, or null when the operator has to choose:
    /// SAP keeps one business partner per currency, except those set to all currencies.
    /// </summary>
    public static string? DocumentCurrency(string? partnerCurrency)
    {
        var currency = partnerCurrency?.Trim();
        return string.IsNullOrEmpty(currency) || currency == AnyCurrency ? null : currency;
    }

    /// <summary>A line's value after its discount — what SAP bills before VAT.</summary>
    public static decimal LineNet(decimal quantity, decimal? unitPrice, decimal? discountPercent)
    {
        var gross = quantity * (unitPrice ?? 0m);
        var discount = discountPercent ?? 0m;
        return discount > 0 ? gross * (1 - discount / 100m) : gross;
    }

    /// <summary>The recent list with <paramref name="cardCode"/> moved to the front, capped.</summary>
    public static List<string> PushRecent(IEnumerable<string>? recent, string? cardCode)
    {
        var list = (recent ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToList();

        if (!string.IsNullOrWhiteSpace(cardCode))
        {
            list.RemoveAll(code => string.Equals(code, cardCode, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, cardCode);
        }

        return list
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(RecentCustomerLimit)
            .ToList();
    }

    /// <summary>
    /// The customer picker's rows: recently invoiced customers first, marked so, then everyone by
    /// code. The picker keeps the caller's order, so leading with them is all it takes.
    /// </summary>
    public static List<NocturnePickerOption> CustomerOptions(
        IEnumerable<BusinessPartnerDto>? partners,
        IReadOnlyList<string>? recentCodes)
    {
        var all = BusinessPartnerOptions.From(partners);
        if (recentCodes is not { Count: > 0 })
        {
            return all;
        }

        var byCode = all.ToDictionary(o => o.Value, StringComparer.OrdinalIgnoreCase);
        var recent = recentCodes
            .Where(byCode.ContainsKey)
            .Select(code => byCode[code])
            .ToList();

        var recentSet = recent.Select(o => o.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return recent
            .Select(o => o with { Hint = string.IsNullOrEmpty(o.Hint) ? "Recent" : $"Recent · {o.Hint}" })
            .Concat(all.Where(o => !recentSet.Contains(o.Value)))
            .ToList();
    }
}
