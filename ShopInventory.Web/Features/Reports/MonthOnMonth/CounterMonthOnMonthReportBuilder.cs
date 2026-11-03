using System.Globalization;
using ShopInventory.Web.Features.ReportProductGroups;
using ShopInventory.Web.Features.Reports.Queries.GetCounterMonthOnMonth;

namespace ShopInventory.Web.Features.Reports.MonthOnMonth;

/// <summary>What the builder needs to know about one item from the product master.</summary>
public sealed record MonthOnMonthItemInfo(string? ItemName, int? ItemsGroupCode);

/// <summary>
/// Turns the API's raw month-on-month figures into the report: product groups, cash vs plastic, product lines.
/// </summary>
/// <remarks>
/// Done here rather than in the API because only the Web holds the product master's item groups (the API's
/// own Products table is never filled). Every amount comes out in USD: ZiG at the rate the caller passes.
/// A product group applies its current members to both months, so the two are measured on the same products.
/// </remarks>
public static class CounterMonthOnMonthReportBuilder
{
    public const string Shops = "shops";
    public const string Vending = "vending";
    public const string NotInProductMaster = "Not in the product master";

    private static readonly HashSet<string> ZigSpellings = new(StringComparer.OrdinalIgnoreCase) { "ZWG", "ZIG", "ZWL" };

    /// <summary>True when the months hold any ZiG sales, which need a rate to be shown in USD.</summary>
    public static bool NeedsZigRate(CounterMonthOnMonthData data) =>
        data.Tenders.Any(t => ZigSpellings.Contains(t.Currency)) || data.Items.Any(i => ZigSpellings.Contains(i.Currency));

    public static CounterMonthOnMonthReport Build(
        CounterMonthOnMonthData data,
        IReadOnlyList<ReportProductGroupDto> groups,
        IReadOnlyDictionary<string, MonthOnMonthItemInfo> items,
        IReadOnlyDictionary<int, string> itemGroupNames,
        IReadOnlyDictionary<string, string> warehouseNames,
        decimal? zigRate,
        DateTime today)
    {
        var notes = new List<string>();
        var unconverted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previous = data.PreviousMonth;
        var current = data.CurrentMonth;

        decimal? Usd(decimal amount, string currency)
        {
            if (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
            {
                return amount;
            }

            if (ZigSpellings.Contains(currency) && zigRate is > 0)
            {
                return amount / zigRate.Value;
            }

            unconverted.Add(currency);
            return null;
        }

        MonthPair Pair(DateTime month, decimal value) => month == previous ? new(value, 0) : new(0, value);

        // Items in USD, keyed by place; an amount in a currency that cannot be converted is left out and named.
        var itemRows = data.Items
            .Select(i => (Row: i, Value: Usd(i.NetValue, i.Currency)))
            .Where(x => x.Value is not null)
            .Select(x => (x.Row, Value: x.Value!.Value, Key: $"{x.Row.Business}|{x.Row.WarehouseCode}"))
            .ToList();
        var tenderRows = data.Tenders
            .Select(t => (Row: t, Gross: Usd(t.Gross, t.Currency), Credited: Usd(t.CreditedAmount, t.Currency)))
            .Where(x => x.Gross is not null)
            .Select(x => (x.Row, Gross: x.Gross!.Value, Credited: x.Credited ?? 0, Key: $"{x.Row.Business}|{x.Row.WarehouseCode}"))
            .ToList();

        var places = data.Places
            .Select(p =>
            {
                var key = $"{p.Business}|{p.WarehouseCode}";
                var takings = tenderRows.Where(t => t.Key == key).Aggregate(default(MonthPair), (sum, t) => sum + Pair(t.Row.Month, t.Gross));
                var credits = tenderRows.Where(t => t.Key == key).Aggregate(default(MonthPair), (sum, t) => sum + Pair(t.Row.Month, t.Credited));
                var itemValue = itemRows.Where(i => i.Key == key).Aggregate(default(MonthPair), (sum, i) => sum + Pair(i.Row.Month, i.Value));
                return new MonthOnMonthPlace(
                    p.Business, p.WarehouseCode, PlaceName(p, warehouseNames), p.FirstSaleDate,
                    p.PreviousTradingDays, p.CurrentTradingDays, takings, credits, itemValue);
            })
            .OrderBy(p => p.Business == Shops ? 0 : 1)
            .ThenByDescending(p => p.Takings.Current)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var placeByKey = places.ToDictionary(p => p.Key);

        string ItemName(string code, string? fallback) =>
            items.TryGetValue(code, out var info) && !string.IsNullOrWhiteSpace(info.ItemName) ? info.ItemName! : fallback ?? code;

        // Product groups: an item belongs when it is named, or when its SAP item group is.
        var sections = new List<MonthOnMonthGroupSection>();
        foreach (var group in groups.Where(g => g.IsActive).OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            var codes = group.Members.Where(m => m.Kind == ReportProductGroupMemberKind.Item)
                .Select(m => m.Code.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
            var itemGroups = group.Members.Where(m => m.Kind == ReportProductGroupMemberKind.ItemGroup)
                .Select(m => int.TryParse(m.Code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
                .Where(n => n is not null).Select(n => n!.Value).ToHashSet();

            bool Belongs(string code) =>
                codes.Contains(code)
                || (items.TryGetValue(code, out var info) && info.ItemsGroupCode is { } g && itemGroups.Contains(g));

            var inGroup = itemRows.Where(i => Belongs(i.Row.ItemCode)).ToList();

            var placeRows = places
                .Where(p => p.ItemValue.Previous != 0 || p.ItemValue.Current != 0)
                .Select(p =>
                {
                    var mine = inGroup.Where(i => i.Key == p.Key).ToList();
                    var qty = mine.Aggregate(default(MonthPair), (sum, i) => sum + Pair(i.Row.Month, i.Row.Quantity));
                    var value = mine.Aggregate(default(MonthPair), (sum, i) => sum + Pair(i.Row.Month, i.Value));
                    return new MonthOnMonthGroupPlaceRow(p, qty, value, ShareOf(value, p.ItemValue));
                })
                .Where(r => r.Value.Previous != 0 || r.Value.Current != 0 || r.Place.Business == Shops)
                .OrderBy(r => r.Place.Business == Shops ? 0 : 1)
                .ThenByDescending(r => r.Value.Current)
                .ToList();

            var itemDetail = inGroup
                .GroupBy(i => (i.Key, i.Row.ItemCode))
                .Select(g => new MonthOnMonthGroupItemRow(
                    placeByKey[g.Key.Key],
                    g.Key.ItemCode,
                    ItemName(g.Key.ItemCode, g.Select(i => i.Row.ItemName).FirstOrDefault(n => n is not null)),
                    g.Aggregate(default(MonthPair), (sum, i) => sum + Pair(i.Row.Month, i.Row.Quantity)),
                    g.Aggregate(default(MonthPair), (sum, i) => sum + Pair(i.Row.Month, i.Value))))
                .OrderBy(r => r.Place.Business == Shops ? 0 : 1)
                .ThenBy(r => r.Place.Name, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(r => r.Value.Current)
                .ToList();

            sections.Add(new MonthOnMonthGroupSection(
                group.Id, group.Name, group.Description, MembersSummary(group, itemGroupNames), placeRows, itemDetail));
        }

        // Cash vs plastic: plastic is the card reader (Swipe); Ecocash and Innbucks are mobile money.
        var otherTenders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var tenders = places
            .Select(p =>
            {
                var mine = tenderRows.Where(t => t.Key == p.Key).ToList();
                MonthPair Sum(Func<string, bool> which, bool count) => mine
                    .Where(t => which(Bucket(t.Row.Tender)))
                    .Aggregate(default(MonthPair), (sum, t) => sum + Pair(t.Row.Month, count ? t.Row.Sales : t.Gross));
                foreach (var t in mine.Where(t => Bucket(t.Row.Tender) == "Other"))
                {
                    otherTenders.Add(t.Row.Tender);
                }

                return new MonthOnMonthTenderRow(
                    p,
                    Sum(b => b == "Cash", false), Sum(b => b == "Plastic", false), Sum(b => b == "Mobile", false), Sum(b => b == "Other", false),
                    Sum(b => b == "Cash", true), Sum(b => b == "Plastic", true), Sum(b => b == "Mobile", true), Sum(b => b == "Other", true),
                    p.Credits);
            })
            .ToList();

        // Product lines: the item's SAP item group, inside each business and at each place.
        string LineOf(string code) =>
            items.TryGetValue(code, out var info) && info.ItemsGroupCode is { } g
                ? itemGroupNames.TryGetValue(g, out var name) ? name : $"Item group {g}"
                : NotInProductMaster;

        var productLines = new List<MonthOnMonthProductLineRow>();
        foreach (var business in new[] { Shops, Vending })
        {
            var rows = itemRows.Where(i => i.Row.Business == business).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            var whole = rows.Aggregate(default(MonthPair), (sum, i) => sum + Pair(i.Row.Month, i.Value));
            productLines.AddRange(rows
                .GroupBy(i => LineOf(i.Row.ItemCode))
                .Select(g => LineRow(business, null, g.Key, g, whole))
                .OrderByDescending(r => r.Value.Current));

            foreach (var place in places.Where(p => p.Business == business))
            {
                productLines.AddRange(rows
                    .Where(i => i.Key == place.Key)
                    .GroupBy(i => LineOf(i.Row.ItemCode))
                    .Select(g => LineRow(business, place, g.Key, g, place.ItemValue))
                    .OrderByDescending(r => r.Value.Current));
            }
        }

        MonthOnMonthProductLineRow LineRow(
            string business, MonthOnMonthPlace? place, string line,
            IEnumerable<(CounterItemRow Row, decimal Value, string Key)> rows, MonthPair whole)
        {
            var list = rows.ToList();
            var value = list.Aggregate(default(MonthPair), (sum, i) => sum + Pair(i.Row.Month, i.Value));
            var qty = list.Aggregate(default(MonthPair), (sum, i) => sum + Pair(i.Row.Month, i.Row.Quantity));
            return new MonthOnMonthProductLineRow(business, place, line, qty, value, ShareOf(value, whole));
        }

        // What a reader must know before comparing the two months. Dates are written in the invariant
        // culture so the report reads the same whatever the server's regional settings are.
        static string Day(DateTime d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        static string MonthName(DateTime d) => d.ToString("MMMM", CultureInfo.InvariantCulture);

        foreach (var month in new[] { previous, current }.OrderBy(m => m))
        {
            if (today >= month && today < month.AddMonths(1))
            {
                notes.Add($"{MonthName(month)} {month.Year} is not over yet: its figures run to {Day(today)}.");
            }
        }

        foreach (var place in places.Where(p => p.FirstSaleDate is not null))
        {
            var first = place.FirstSaleDate!.Value.Date;
            foreach (var month in new[] { previous, current })
            {
                if (first > month && first < month.AddMonths(1))
                {
                    notes.Add($"{place.Name} first sold on the till on {Day(first)}, so its {MonthName(month)} covers only part of the month.");
                }
                else if (first >= month.AddMonths(1) && month == previous && previous < current)
                {
                    notes.Add($"{place.Name} first sold on {Day(first)}: it has nothing to compare in {MonthName(month)}.");
                }
            }
        }

        if (zigRate is > 0 && NeedsZigRate(data))
        {
            notes.Add($"ZiG sales are shown in USD at {zigRate.Value.ToString("0.####", CultureInfo.InvariantCulture)} ZiG to 1 USD.");
        }

        if (unconverted.Count > 0)
        {
            notes.Add($"Left out because they could not be shown in USD: sales in {string.Join(", ", unconverted.OrderBy(c => c))}.");
        }

        return new CounterMonthOnMonthReport(
            previous, current, zigRate, notes, places, sections, tenders, productLines, otherTenders.ToList());
    }

    /// <summary>Which column a stored tender lands in.</summary>
    public static string Bucket(string tender) => tender switch
    {
        "Cash" => "Cash",
        "Swipe" => "Plastic",
        "Ecocash" or "Innbucks" => "Mobile",
        _ => "Other"
    };

    private static MonthPair ShareOf(MonthPair part, MonthPair whole) => new(
        whole.Previous == 0 ? 0 : part.Previous / whole.Previous,
        whole.Current == 0 ? 0 : part.Current / whole.Current);

    private static string PlaceName(CounterPlaceRow place, IReadOnlyDictionary<string, string> warehouseNames) =>
        !string.IsNullOrWhiteSpace(place.ShopName) ? place.ShopName!
        : warehouseNames.TryGetValue(place.WarehouseCode, out var name) && !string.IsNullOrWhiteSpace(name) ? name
        : place.WarehouseCode;

    private static string MembersSummary(ReportProductGroupDto group, IReadOnlyDictionary<int, string> itemGroupNames)
    {
        var parts = new List<string>();
        foreach (var member in group.Members.Where(m => m.Kind == ReportProductGroupMemberKind.ItemGroup))
        {
            var label = int.TryParse(member.Code, out var n) && itemGroupNames.TryGetValue(n, out var name) ? name : member.Name ?? member.Code;
            parts.Add($"every item in SAP item group {member.Code} ({label})");
        }

        var count = group.Members.Count(m => m.Kind == ReportProductGroupMemberKind.Item);
        if (count > 0)
        {
            parts.Add(count == 1 ? "1 named item" : $"{count} named items");
        }

        return parts.Count == 0 ? "No products yet" : string.Join(" + ", parts);
    }
}
