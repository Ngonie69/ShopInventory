using System.Globalization;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Vending;

/// <summary>
/// The figures on a vendor's page, worked out from the sales the route-customer endpoint already returns:
/// month to date, the last fortnight day by day, the product mix and how far posting has got.
///
/// Money is never added across currencies here, for the same reason the API never does. A vendor who
/// took USD and ZWG took no single number, so every figure is read in one currency — the one the vendor
/// sells in most — and the others are counted, not converted, so the page can say they were left out.
///
/// Days are the sale's own date (<see cref="RouteCustomerSaleModel.SoldAt"/> is the sale's DocDate, a bare
/// day), so nothing here converts time zones. "Today" is the caller's, in CAT.
/// </summary>
public static class VendorSalesDigest
{
    public const int DailyWindowDays = 14;

    public enum PostingState { Posted, Awaiting, Failed, Excluded }

    public sealed record MonthToDate(
        string? Currency,
        decimal Gross,
        int SaleCount,
        int LineCount,
        decimal Units,
        IReadOnlyList<RouteCustomerSalesTotalsModel> OtherCurrencies);

    public sealed record Day(DateTime Date, decimal Gross, int SaleCount);

    public sealed record MixItem(string ItemCode, string Name, decimal Units, string? UoMCode, decimal Value);

    public sealed record Posting(int Posted, int Awaiting, int Failed, int Excluded)
    {
        public int Total => Posted + Awaiting + Failed + Excluded;
    }

    /// <summary>The first day of the month <paramref name="today"/> is in.</summary>
    public static DateTime MonthStart(DateTime today) => new(today.Year, today.Month, 1);

    /// <summary>
    /// The earliest day a vendor page has to read: far enough back for both the month so far and the
    /// daily chart, whichever starts first.
    /// </summary>
    public static DateTime WindowStart(DateTime today)
    {
        var monthStart = MonthStart(today.Date);
        var chartStart = today.Date.AddDays(-(DailyWindowDays - 1));
        return monthStart < chartStart ? monthStart : chartStart;
    }

    /// <summary>
    /// The currency most of these sales were made in, by count and then by value. Null when there are
    /// none, which a caller should read as "nothing to show", not as a default currency.
    /// </summary>
    public static string? PrimaryCurrency(IEnumerable<RouteCustomerSalesTotalsModel> totals) =>
        totals
            .Where(total => total.SaleCount > 0 || total.Gross != 0)
            .OrderByDescending(total => total.SaleCount)
            .ThenByDescending(total => total.Gross)
            .ThenBy(total => total.Currency, StringComparer.Ordinal)
            .Select(total => total.Currency)
            .FirstOrDefault();

    public static string? PrimaryCurrency(IEnumerable<RouteCustomerSaleModel> sales) =>
        PrimaryCurrency(SumByCurrency(sales));

    public static List<RouteCustomerSalesTotalsModel> SumByCurrency(IEnumerable<RouteCustomerSaleModel> sales) =>
        sales
            .GroupBy(sale => sale.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RouteCustomerSalesTotalsModel
            {
                Currency = group.Key,
                SaleCount = group.Count(),
                Gross = group.Sum(sale => sale.Total),
                Vat = group.Sum(sale => sale.VatAmount),
                AmountPaid = group.Sum(sale => sale.AmountPaid)
            })
            .OrderByDescending(total => total.SaleCount)
            .ThenBy(total => total.Currency, StringComparer.Ordinal)
            .ToList();

    public static MonthToDate SummariseMonth(IEnumerable<RouteCustomerSaleModel> sales, DateTime today)
    {
        var monthStart = MonthStart(today.Date);
        var inMonth = sales.Where(sale => sale.SoldAt.Date >= monthStart && sale.SoldAt.Date <= today.Date).ToList();
        var totals = SumByCurrency(inMonth);
        var currency = PrimaryCurrency(totals);
        var primary = inMonth.Where(sale => SameCurrency(sale.Currency, currency)).ToList();

        return new MonthToDate(
            currency,
            primary.Sum(sale => sale.Total),
            primary.Count,
            primary.Sum(sale => sale.Lines.Count),
            primary.SelectMany(sale => sale.Lines).Sum(line => line.Quantity),
            totals.Where(total => !SameCurrency(total.Currency, currency)).ToList());
    }

    /// <summary>
    /// One entry per calendar day for the <see cref="DailyWindowDays"/> days ending today, oldest first,
    /// with the quiet days present as zero — a missing bar would close the gap the chart exists to show.
    /// </summary>
    public static List<Day> Daily(IEnumerable<RouteCustomerSaleModel> sales, DateTime today, string? currency)
    {
        var byDay = sales
            .Where(sale => SameCurrency(sale.Currency, currency))
            .GroupBy(sale => sale.SoldAt.Date)
            .ToDictionary(group => group.Key, group => (Gross: group.Sum(sale => sale.Total), Count: group.Count()));

        return Enumerable.Range(0, DailyWindowDays)
            .Select(offset => today.Date.AddDays(offset - (DailyWindowDays - 1)))
            .Select(day => byDay.TryGetValue(day, out var totals)
                ? new Day(day, totals.Gross, totals.Count)
                : new Day(day, 0m, 0))
            .ToList();
    }

    /// <summary>
    /// What the vendor bought this month, largest value first. Value is the line total, which is before
    /// VAT, so the column will not add up to the month's gross — the page says so.
    /// </summary>
    public static List<MixItem> ProductMix(IEnumerable<RouteCustomerSaleModel> sales, DateTime today, string? currency, int top)
    {
        var monthStart = MonthStart(today.Date);

        return sales
            .Where(sale => SameCurrency(sale.Currency, currency))
            .Where(sale => sale.SoldAt.Date >= monthStart && sale.SoldAt.Date <= today.Date)
            .SelectMany(sale => sale.Lines)
            .GroupBy(line => line.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MixItem(
                group.Key,
                group.Select(line => line.ItemDescription).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key,
                group.Sum(line => line.Quantity),
                group.Select(line => line.UoMCode).FirstOrDefault(uom => !string.IsNullOrWhiteSpace(uom)),
                group.Sum(line => line.LineTotal)))
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.ItemCode, StringComparer.Ordinal)
            .Take(top)
            .ToList();
    }

    /// <summary>
    /// Where a sale has got to in SAP, read from the API's status wording
    /// (RouteCustomerSalesReporting.DescribeOfflineSaleStatus). A receipt problem trailing a posted sale
    /// still needs someone, so it counts as failed rather than posted.
    /// </summary>
    public static PostingState PostingOf(string? status)
    {
        var text = status ?? string.Empty;
        if (text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("broken", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unsignable", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not yet with ZIMRA", StringComparison.OrdinalIgnoreCase))
        {
            return PostingState.Failed;
        }

        if (text.StartsWith("Excluded", StringComparison.OrdinalIgnoreCase))
        {
            return PostingState.Excluded;
        }

        return text.StartsWith("Posted", StringComparison.OrdinalIgnoreCase)
            ? PostingState.Posted
            : PostingState.Awaiting;
    }

    public static string StateChip(PostingState state) => state switch
    {
        PostingState.Posted => "shop-chip-good",
        PostingState.Failed => "shop-chip-bad",
        _ => "shop-chip-neutral"
    };

    public static string StateLabel(PostingState state) => state switch
    {
        PostingState.Posted => "Posted",
        PostingState.Failed => "Needs attention",
        PostingState.Excluded => "Excluded",
        _ => "Awaiting"
    };

    public static Posting PostingSince(IEnumerable<RouteCustomerSaleModel> sales, DateTime from)
    {
        var states = sales.Where(sale => sale.SoldAt.Date >= from.Date).Select(sale => PostingOf(sale.Status)).ToList();
        return new Posting(
            states.Count(state => state == PostingState.Posted),
            states.Count(state => state == PostingState.Awaiting),
            states.Count(state => state == PostingState.Failed),
            states.Count(state => state == PostingState.Excluded));
    }

    public static string Money(string? currency, decimal amount, int decimals = 2)
    {
        var figure = amount.ToString($"N{decimals}", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(currency) ? figure : $"{currency} {figure}";
    }

    /// <summary>
    /// "USD 1,204.50", or "USD 1,204.50 + ZWG" when other currencies traded too. Dash when nothing did.
    /// <paramref name="decimals"/> 0 is for a depot card, where cents only crowd the figure.
    /// </summary>
    public static string MoneyLabel(IReadOnlyCollection<RouteCustomerSalesTotalsModel>? totals, int decimals = 2)
    {
        if (totals is null)
        {
            return "—";
        }

        var currency = PrimaryCurrency(totals);
        if (currency is null)
        {
            return "—";
        }

        var gross = totals.Where(total => SameCurrency(total.Currency, currency)).Sum(total => total.Gross);
        var others = totals
            .Where(total => !SameCurrency(total.Currency, currency) && (total.SaleCount > 0 || total.Gross != 0))
            .Select(total => total.Currency)
            .ToList();

        return others.Count == 0
            ? Money(currency, gross, decimals)
            : $"{Money(currency, gross, decimals)} + {string.Join(", ", others)}";
    }

    public static string Initials(string? name, string? surname = null)
    {
        var parts = $"{name} {surname}"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => char.IsLetterOrDigit(part[0]))
            .ToList();

        return parts.Count switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => string.Concat(parts[0][0], parts[^1][0]).ToUpperInvariant()
        };
    }

    /// <summary>"Today", "Yesterday", "4 days ago", then the date once it is more than a week old.</summary>
    public static string LastSaleLabel(DateTime? lastSale, DateTime today)
    {
        if (lastSale is not { } day)
        {
            return "Never";
        }

        var days = (int)(today.Date - day.Date).TotalDays;
        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            <= 7 => $"{days} days ago",
            _ => day.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
        };
    }

    private static bool SameCurrency(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
