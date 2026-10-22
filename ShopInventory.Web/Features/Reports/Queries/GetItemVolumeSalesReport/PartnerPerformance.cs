using System.Globalization;

namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// One window read against the same dates in earlier years, partner by partner and period by period.
/// </summary>
/// <remarks>
/// <para>
/// Built from one item volume report per year rather than one report spanning all of them. The
/// years are the same calendar dates shifted back — 01 Jan – 16 Oct, then the same in each year
/// before — so every window is the same length (a day longer across a 29 February) and the months
/// between them are never read. A single spanning report would ask SAP for the gaps too.
/// </para>
/// <para>
/// A window longer than a year is the exception: the same dates a year back would overlap it, so it
/// is cut into its <see cref="CalendarYears"/> instead — 2024, 2025 and 2026 so far, side by side.
/// </para>
/// <para>
/// Index 0 is always the window that was asked for, or its newest calendar year; 1 is the year before it, 2 the year before that.
/// Every figure is the report's own, so it is net of the credit notes raised in that year's window,
/// and a credited period stays negative here as it does on the other lenses.
/// </para>
/// <para>
/// Money is USD, every document included. A partner that traded in ZiG as well has those documents
/// in its figures at the USD SAP booked them at — each document's own rate, as SAP's DocTotal carries
/// it — so a partner billed in both currencies reads as one partner rather than two half-partners.
/// </para>
/// </remarks>
public sealed class PartnerPerformance
{
    /// <summary>One year's window, newest first.</summary>
    public sealed record Window(string Label, DateTime FromUtc, DateTime ToUtc);

    /// <summary>What one partner, or all of them, came to in one window.</summary>
    public sealed record Figures(decimal Revenue, decimal Volume, decimal Credited, int Invoices, int CreditNotes)
    {
        public static readonly Figures Empty = new(0m, 0m, 0m, 0, 0);

        /// <summary>Revenue over volume — price and mix in one number. Null when nothing converted to volume.</summary>
        public decimal? RevenuePerLitre => Volume == 0 ? null : Revenue / Volume;

        public bool Traded => Invoices > 0 || CreditNotes > 0;

        /// <summary>SAP's booked gross profit, net of credit notes, in USD like <c>Revenue</c>.</summary>
        public decimal GrossProfit { get; init; }

        /// <summary><see cref="GrossProfit"/> over <c>Revenue</c>, to one place; null with no revenue.</summary>
        public decimal? MarginPercent => Revenue == 0
            ? null
            : Math.Round(GrossProfit / Revenue * 100m, 1, MidpointRounding.AwayFromZero);

        public Figures Add(Figures other) => new(
            Revenue + other.Revenue,
            Volume + other.Volume,
            Credited + other.Credited,
            Invoices + other.Invoices,
            CreditNotes + other.CreditNotes)
        {
            GrossProfit = GrossProfit + other.GrossProfit
        };
    }

    /// <summary>A partner across every window, in the order of <see cref="Windows"/>.</summary>
    public sealed record Partner(string Code, string Name, IReadOnlyList<Figures> Years);

    /// <summary>
    /// One row of the period table: the same slice of each year's window, side by side. Its label is
    /// the asked-for window's, since the earlier windows are the same dates. <c>ShortLabel</c> is the
    /// same heading cut to fit under a chart: "Oct", "Q3".
    /// </summary>
    public sealed record Period(string Label, string ShortLabel, IReadOnlyList<decimal> Revenue, IReadOnlyList<decimal> Volume)
    {
        /// <summary>
        /// Per window, whether the row falls inside it at all. Only calendar years have rows a
        /// window does not reach — November of a year that ends in October — and null means all do.
        /// </summary>
        public IReadOnlyList<bool>? InWindow { get; init; }
    }

    private readonly IReadOnlyList<GetItemVolumeSalesReportResult> reports;

    private PartnerPerformance(
        IReadOnlyList<GetItemVolumeSalesReportResult> reports,
        IReadOnlyList<Window> windows,
        IReadOnlyList<Partner> partners,
        bool byCalendarYear)
    {
        this.reports = reports;
        Windows = windows;
        ByCalendarYear = byCalendarYear;
        Partners = partners;
        Totals = windows
            .Select((_, year) => partners.Aggregate(Figures.Empty, (sum, partner) => sum.Add(partner.Years[year])))
            .ToList();
    }

    public IReadOnlyList<Window> Windows { get; }

    /// <summary>Every partner that traded in at least one window, largest current revenue first.</summary>
    public IReadOnlyList<Partner> Partners { get; }

    /// <summary>All the partners together, per window.</summary>
    public IReadOnlyList<Figures> Totals { get; }

    /// <summary>The same dates <paramref name="yearsBack"/> years earlier. 29 February lands on the 28th.</summary>
    public static (DateTime From, DateTime To) Shift(DateTime from, DateTime to, int yearsBack) =>
        (from.AddYears(-yearsBack), to.AddYears(-yearsBack));

    /// <summary>The most windows the lens lays side by side: the asked-for one and two before it.</summary>
    public const int MaxWindows = 3;

    /// <summary>
    /// A window longer than a year, cut at each New Year into one window per calendar year, newest
    /// first: 01 Jan 2024 – 16 Oct 2026 is 2026 to 16 Oct, all of 2025 and all of 2024. The first and
    /// last keep the window's own ends. Only the newest <see cref="MaxWindows"/> years are kept. Null
    /// when the window fits in a year, which is compared against the same dates shifted back instead.
    /// </summary>
    /// <remarks>
    /// The same dates a year earlier overlap a longer window: 2024–26 against 2023–25 shares all of
    /// 2025, so a partner that traded only in the overlap reads the same figure twice at 0.0%, and
    /// neither column is a year.
    /// </remarks>
    public static IReadOnlyList<Window>? CalendarYears(DateTime from, DateTime to)
    {
        if (from.Date >= to.Date.AddYears(-1).AddDays(1))
        {
            return null;
        }

        var windows = new List<Window>();

        for (var year = to.Year; year >= from.Year && windows.Count < MaxWindows; year--)
        {
            var yearFrom = year == from.Year ? from : new DateTime(year, 1, 1, 0, 0, 0, from.Kind);
            var yearTo = year == to.Year ? to : new DateTime(year, 12, 31, 0, 0, 0, to.Kind);
            windows.Add(new Window(year.ToString(CultureInfo.InvariantCulture), yearFrom, yearTo));
        }

        return windows;
    }

    /// <summary>
    /// Whether the windows are calendar years of one long window rather than the same dates in
    /// earlier years. The rows then line up by month or quarter, and a year need not be whole.
    /// </summary>
    public bool ByCalendarYear { get; }

    /// <summary>The years that do not run the whole calendar year — "2026 runs 01 Jan – 16 Oct". Empty when every one does.</summary>
    public IReadOnlyList<string> PartYears => !ByCalendarYear
        ? Array.Empty<string>()
        : Windows
            .Where(window => window.FromUtc.Date != new DateTime(window.FromUtc.Year, 1, 1)
                || window.ToUtc.Date != new DateTime(window.ToUtc.Year, 12, 31))
            .Select(window => $"{window.Label} runs {window.FromUtc.ToString("dd MMM", CultureInfo.InvariantCulture)} – {window.ToUtc.ToString("dd MMM", CultureInfo.InvariantCulture)}")
            .ToList();

    /// <param name="newestFirst">The asked-for window first, then each earlier year in turn.</param>
    /// <param name="byCalendarYear">The reports are the <see cref="CalendarYears"/> of one long window.</param>
    public static PartnerPerformance Build(IReadOnlyList<GetItemVolumeSalesReportResult> newestFirst, bool byCalendarYear = false)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);

        if (newestFirst.Count == 0)
        {
            throw new ArgumentException("At least one window is required.", nameof(newestFirst));
        }

        var windows = newestFirst
            .Select(report => new Window(YearLabel(report.FromDateUtc, report.ToDateUtc), report.FromDateUtc, report.ToDateUtc))
            .ToList();

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var figures = new Dictionary<string, Figures[]>(StringComparer.OrdinalIgnoreCase);

        for (var year = 0; year < newestFirst.Count; year++)
        {
            foreach (var account in newestFirst[year].AccountTotals)
            {
                // The newest window names a partner, so a renamed account reads as it does today.
                if (!names.ContainsKey(account.CardCode) && !string.IsNullOrWhiteSpace(account.CardName))
                {
                    names[account.CardCode] = account.CardName;
                }

                if (!figures.TryGetValue(account.CardCode, out var years))
                {
                    years = Enumerable.Repeat(Figures.Empty, newestFirst.Count).ToArray();
                    figures[account.CardCode] = years;
                }

                years[year] = years[year].Add(FiguresOf(account));
            }
        }

        var partners = figures
            .Where(pair => pair.Value.Any(year => year.Traded))
            .Select(pair => new Partner(
                pair.Key,
                names.TryGetValue(pair.Key, out var name) ? name : pair.Key,
                pair.Value))
            .OrderByDescending(partner => partner.Years[0].Revenue)
            .ThenByDescending(partner => partner.Years.Count > 1 ? partner.Years[1].Revenue : 0m)
            .ThenBy(partner => partner.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PartnerPerformance(newestFirst, windows, partners, byCalendarYear);
    }

    /// <summary>
    /// The period rows for one partner, or for every partner together when <paramref name="cardCode"/>
    /// is null. Periods line up by position: the earlier windows are the same dates, so the n-th
    /// period of each is the same slice of its year.
    /// </summary>
    public IReadOnlyList<Period> PeriodsFor(string? cardCode)
    {
        if (ByCalendarYear && reports[0].Grouping is ItemVolumeSalesGrouping.Monthly or ItemVolumeSalesGrouping.Quarterly or ItemVolumeSalesGrouping.Total)
        {
            return CalendarPeriodsFor(cardCode);
        }

        var current = reports[0];
        var rows = new List<Period>(current.Periods.Count);

        for (var index = 0; index < current.Periods.Count; index++)
        {
            var revenue = new decimal[reports.Count];
            var volume = new decimal[reports.Count];

            for (var year = 0; year < reports.Count; year++)
            {
                if (index >= reports[year].Periods.Count)
                {
                    continue;
                }

                foreach (var account in reports[year].Periods[index].Accounts)
                {
                    if (cardCode is not null && !string.Equals(account.CardCode, cardCode, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    revenue[year] += account.NetRevenueUsd;
                    volume[year] += account.NetVolume;
                }
            }

            rows.Add(new Period(
                PeriodLabel(current, current.Periods[index]),
                ShortPeriodLabel(current, current.Periods[index]),
                revenue,
                volume));
        }

        return rows;
    }

    /// <summary>
    /// Calendar years start where they like — 2024 from March, 2026 from January — so their periods
    /// line up by name rather than position: March against March, Q2 against Q2, every month any of
    /// the years reached, in calendar order.
    /// </summary>
    private IReadOnlyList<Period> CalendarPeriodsFor(string? cardCode)
    {
        var grouping = reports[0].Grouping;
        var rows = new SortedDictionary<int, (decimal[] Revenue, decimal[] Volume)>();

        for (var year = 0; year < reports.Count; year++)
        {
            foreach (var period in reports[year].Periods)
            {
                var key = CalendarKey(grouping, period.PeriodStartUtc);

                if (!rows.TryGetValue(key, out var row))
                {
                    row = (new decimal[reports.Count], new decimal[reports.Count]);
                    rows[key] = row;
                }

                foreach (var account in period.Accounts)
                {
                    if (cardCode is not null && !string.Equals(account.CardCode, cardCode, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    row.Revenue[year] += account.NetRevenueUsd;
                    row.Volume[year] += account.NetVolume;
                }
            }
        }

        return rows
            .Select(pair => new Period(
                CalendarLabel(grouping, pair.Key, shortLabel: false),
                CalendarLabel(grouping, pair.Key, shortLabel: true),
                pair.Value.Revenue,
                pair.Value.Volume)
            {
                InWindow = Windows.Select(window => Reaches(grouping, pair.Key, window)).ToList()
            })
            .ToList();
    }

    /// <summary>The month (1–12) or quarter (1–4) a period starts in; 0 for a whole-window total.</summary>
    private static int CalendarKey(ItemVolumeSalesGrouping grouping, DateTime start) => grouping switch
    {
        ItemVolumeSalesGrouping.Monthly => start.Month,
        ItemVolumeSalesGrouping.Quarterly => ((start.Month - 1) / 3) + 1,
        _ => 0
    };

    private static string CalendarLabel(ItemVolumeSalesGrouping grouping, int key, bool shortLabel) => grouping switch
    {
        ItemVolumeSalesGrouping.Monthly => new DateTime(2000, key, 1).ToString(shortLabel ? "MMM" : "MMMM", CultureInfo.InvariantCulture),
        ItemVolumeSalesGrouping.Quarterly => string.Create(CultureInfo.InvariantCulture, $"Q{key}"),
        _ => shortLabel ? "Year" : "Whole year"
    };

    /// <summary>Whether <paramref name="window"/> takes in any day of the month or quarter <paramref name="key"/> names.</summary>
    private static bool Reaches(ItemVolumeSalesGrouping grouping, int key, Window window)
    {
        var (firstMonth, months) = grouping switch
        {
            ItemVolumeSalesGrouping.Monthly => (key, 1),
            ItemVolumeSalesGrouping.Quarterly => (((key - 1) * 3) + 1, 3),
            _ => (1, 12)
        };

        var start = new DateTime(window.FromUtc.Year, firstMonth, 1);
        var end = start.AddMonths(months).AddDays(-1);
        return start <= window.ToUtc.Date && end >= window.FromUtc.Date;
    }

    /// <summary>The partner's figures per window, or every partner's when <paramref name="cardCode"/> is null.</summary>
    public IReadOnlyList<Figures> FiguresFor(string? cardCode) =>
        cardCode is null
            ? Totals
            : Partners.FirstOrDefault(partner => string.Equals(partner.Code, cardCode, StringComparison.OrdinalIgnoreCase))?.Years
              ?? Windows.Select(_ => Figures.Empty).ToList();

    /// <summary>
    /// The change from <paramref name="basis"/> to <paramref name="current"/>, in percent. Null when
    /// there was nothing to grow from — a partner with no trade a year ago is new, not infinitely up.
    /// </summary>
    public static decimal? Change(decimal current, decimal basis) =>
        basis == 0 ? null : (current - basis) / Math.Abs(basis) * 100m;

    /// <summary>"2026" for a window inside one year; "2025–26" for one that crosses New Year.</summary>
    public static string YearLabel(DateTime from, DateTime to) =>
        from.Year == to.Year
            ? from.Year.ToString(CultureInfo.InvariantCulture)
            : $"{from.Year.ToString(CultureInfo.InvariantCulture)}–{(to.Year % 100).ToString("00", CultureInfo.InvariantCulture)}";

    private static Figures FiguresOf(ItemVolumeSalesAccountResult account) => new(
        account.NetRevenueUsd,
        account.NetVolume,
        account.CreditedSalesUsd,
        account.InvoiceCount,
        account.CreditNoteCount)
    {
        GrossProfit = account.NetGrossProfitUsd
    };

    private static string ShortPeriodLabel(GetItemVolumeSalesReportResult report, ItemVolumeSalesPeriodResult period)
    {
        var spansYears = report.FromDateUtc.Year != report.ToDateUtc.Year;

        return report.Grouping switch
        {
            ItemVolumeSalesGrouping.Total => "Window",
            ItemVolumeSalesGrouping.Quarterly => string.Create(
                CultureInfo.InvariantCulture,
                $"Q{((period.PeriodStartUtc.Month - 1) / 3) + 1}{(spansYears ? $" {period.PeriodStartUtc:yy}" : string.Empty)}"),
            ItemVolumeSalesGrouping.Monthly => period.PeriodStartUtc.ToString(spansYears ? "MMM yy" : "MMM", CultureInfo.InvariantCulture),
            _ => period.PeriodStartUtc.ToString("dd MMM", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// A row heading. A period the window cuts into says where it was cut, because "October" against
    /// "October" reads as a whole month each year when it is the first sixteen days of each.
    /// </summary>
    private static string PeriodLabel(GetItemVolumeSalesReportResult report, ItemVolumeSalesPeriodResult period)
    {
        var spansYears = report.FromDateUtc.Year != report.ToDateUtc.Year;

        var name = report.Grouping switch
        {
            ItemVolumeSalesGrouping.Total => "Whole window",
            ItemVolumeSalesGrouping.Quarterly => string.Create(
                CultureInfo.InvariantCulture,
                $"Q{((period.PeriodStartUtc.Month - 1) / 3) + 1}{(spansYears ? $" {period.PeriodStartUtc:yyyy}" : string.Empty)}"),
            ItemVolumeSalesGrouping.Monthly => period.PeriodStartUtc.ToString(spansYears ? "MMMM yyyy" : "MMMM", CultureInfo.InvariantCulture),
            _ => period.Label
        };

        if (report.Grouping is ItemVolumeSalesGrouping.Total or ItemVolumeSalesGrouping.Daily)
        {
            return name;
        }

        if (period.PeriodStartUtc.Date < report.FromDateUtc.Date)
        {
            return $"{name} (from {report.FromDateUtc.ToString("dd MMM", CultureInfo.InvariantCulture)})";
        }

        if (period.PeriodEndUtc.Date > report.ToDateUtc.Date)
        {
            return $"{name} (to {report.ToDateUtc.ToString("dd MMM", CultureInfo.InvariantCulture)})";
        }

        return name;
    }
}
