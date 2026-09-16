using System.Globalization;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Vending;

/// <summary>
/// The figures on a vendor's page, worked out from the sales the route-customer endpoint already returns:
/// a window's totals, the product mix inside it, the window day by day and how far posting has got.
///
/// The window is the caller's. /vending/vendors/{id} lets the reader pick one and hands the whole read
/// here; /vending/sales still asks month to date, and the <c>…Month</c> and <c>…Since</c> methods are the
/// same summaries with a clip in front of them, not second implementations.
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

    /// <summary>
    /// Days without a sale after which a vendor reads as dormant rather than trading. Mirrors the API's
    /// <c>RouteCustomerSalesReporting.DefaultDormantDays</c>, which is what the depot list is measured
    /// against — a vendor must not be dormant on one page and active on the other.
    /// </summary>
    public const int DormantDays = 30;

    public enum PostingState { Posted, Awaiting, Failed, Excluded }

    /// <summary>
    /// What one window came to, in the one currency the vendor mostly sells in. The others are counted
    /// beside it rather than converted into it, so a page can say they were left out.
    /// </summary>
    public sealed record Window(
        string? Currency,
        decimal Gross,
        decimal Vat,
        decimal Paid,
        int SaleCount,
        int LineCount,
        decimal Units,
        IReadOnlyList<RouteCustomerSalesTotalsModel> OtherCurrencies);

    public sealed record Day(DateTime Date, decimal Gross, int SaleCount);

    public sealed record MixItem(string ItemCode, string Name, decimal Units, string? UoMCode, decimal Value, int Lines);

    public sealed record Posting(int Posted, int Awaiting, int Failed, int Excluded)
    {
        public int Total => Posted + Awaiting + Failed + Excluded;
    }

    /// <summary>One day of the activity list: its date, the heading over it, and the sales under it.</summary>
    public sealed record ActivityDay(DateTime Date, string Label, IReadOnlyList<RouteCustomerSaleModel> Sales);

    /// <summary>How a vendor reads at a glance. See <see cref="StandingOf"/> for why this is four states.</summary>
    public enum Standing { Trading, Dormant, NeverSold, Removed }

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

    /// <summary>
    /// Everything given, summed. The caller decides the window — the sales endpoint has already clipped
    /// to one — so nothing here filters by date; <see cref="SummariseMonth"/> is this with a clip in
    /// front of it.
    /// </summary>
    public static Window Summarise(IEnumerable<RouteCustomerSaleModel> sales)
    {
        var all = sales as IReadOnlyCollection<RouteCustomerSaleModel> ?? sales.ToList();
        var totals = SumByCurrency(all);
        var currency = PrimaryCurrency(totals);
        var primary = all.Where(sale => SameCurrency(sale.Currency, currency)).ToList();

        return new Window(
            currency,
            primary.Sum(sale => sale.Total),
            primary.Sum(sale => sale.VatAmount),
            primary.Sum(sale => sale.AmountPaid),
            primary.Count,
            primary.Sum(sale => sale.Lines.Count),
            primary.SelectMany(sale => sale.Lines).Sum(line => line.Quantity),
            totals.Where(total => !SameCurrency(total.Currency, currency)).ToList());
    }

    public static Window SummariseMonth(IEnumerable<RouteCustomerSaleModel> sales, DateTime today)
    {
        var monthStart = MonthStart(today.Date);
        return Summarise(sales.Where(sale => sale.SoldAt.Date >= monthStart && sale.SoldAt.Date <= today.Date));
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
        return ProductMix(
            sales.Where(sale => sale.SoldAt.Date >= monthStart && sale.SoldAt.Date <= today.Date),
            currency,
            top);
    }

    /// <summary>
    /// The same breakdown over whatever window the caller has already read, largest value first. Only the
    /// primary currency's sales count, because the column is one number per item and two currencies are
    /// not one number.
    /// </summary>
    public static List<MixItem> ProductMix(IEnumerable<RouteCustomerSaleModel> sales, string? currency, int top) =>
        sales
            .Where(sale => SameCurrency(sale.Currency, currency))
            .SelectMany(sale => sale.Lines)
            .GroupBy(line => line.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MixItem(
                group.Key,
                group.Select(line => line.ItemDescription).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key,
                group.Sum(line => line.Quantity),
                group.Select(line => line.UoMCode).FirstOrDefault(uom => !string.IsNullOrWhiteSpace(uom)),
                group.Sum(line => line.LineTotal),
                group.Count()))
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.ItemCode, StringComparer.Ordinal)
            .Take(top)
            .ToList();

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

    /// <summary>
    /// The window's sales as the page lists them: newest day first, and the sales of a day together
    /// under it.
    /// </summary>
    /// <remarks>
    /// There is no time of day to sort within a day by. A vending sale is a desktop sale and its
    /// <c>DocDate</c> is a SQL <c>date</c>, so every sale on a day arrives at midnight; the invoice
    /// number is the only order the day has, and it is the order the till rang them up in.
    /// </remarks>
    public static List<ActivityDay> ActivityByDay(IEnumerable<RouteCustomerSaleModel> sales, int maxDays) =>
        sales
            .GroupBy(sale => sale.SoldAt.Date)
            .OrderByDescending(group => group.Key)
            .Take(maxDays)
            .Select(group => new ActivityDay(
                group.Key,
                group.Key.ToString("ddd dd MMM yyyy", CultureInfo.InvariantCulture),
                group
                    .OrderByDescending(sale => sale.SapDocNum ?? 0)
                    .ThenByDescending(sale => sale.Reference, StringComparer.Ordinal)
                    .ToList()))
            .ToList();

    /// <summary>
    /// What was on a sale, in a line: "Milk 2L x22, Maheu 500ml x18 +3 more". The item's own description
    /// where it has one, its code where it does not — never a blank, which would read as a sale of
    /// nothing.
    /// </summary>
    public static string ItemSummary(RouteCustomerSaleModel sale, int max)
    {
        if (sale.Lines.Count == 0)
        {
            return "No lines on this sale";
        }

        var named = sale.Lines
            .Take(max)
            .Select(line => $"{Describe(line)} ×{Quantity(line.Quantity)}");

        var rest = sale.Lines.Count - max;
        return rest > 0
            ? $"{string.Join(", ", named)} +{rest} more"
            : string.Join(", ", named);
    }

    public static string Describe(RouteCustomerSaleLineModel line) =>
        string.IsNullOrWhiteSpace(line.ItemDescription) ? line.ItemCode : line.ItemDescription;

    /// <summary>A quantity with cents only when it has them: "64", not "64.00", but "1.5" stays "1.50".</summary>
    public static string Quantity(decimal units) =>
        units == decimal.Truncate(units)
            ? units.ToString("N0", CultureInfo.InvariantCulture)
            : units.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>"19 Jun - 16 Sep 2026", with the year said once when both ends share it.</summary>
    public static string WindowLabel(DateTime from, DateTime to)
    {
        var left = from.Year == to.Year
            ? from.ToString("dd MMM", CultureInfo.InvariantCulture)
            : from.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

        return $"{left} – {to.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Where a vendor stands, which is not the same question as whether the record is active. A removed
    /// vendor is off the till whatever they last sold; an active one who has never sold is not the same
    /// finding as one who has stopped.
    /// </summary>
    public static Standing StandingOf(bool isActive, DateTime? lastSaleAt, DateTime today)
    {
        if (!isActive)
        {
            return Standing.Removed;
        }

        if (lastSaleAt is not { } last)
        {
            return Standing.NeverSold;
        }

        return (today.Date - last.Date).TotalDays > DormantDays ? Standing.Dormant : Standing.Trading;
    }

    private static bool SameCurrency(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
