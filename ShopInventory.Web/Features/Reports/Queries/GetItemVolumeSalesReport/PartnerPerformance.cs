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
/// Index 0 is always the window that was asked for; 1 is the year before it, 2 the year before that.
/// Every figure is the report's own, so it is net of the credit notes raised in that year's window,
/// and a credited period stays negative here as it does on the other lenses.
/// </para>
/// <para>
/// Money is read in one currency. The two are never added together, for the reason the revenue lens
/// gives: no rate on the document would make one figure of them honest.
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

        public Figures Add(Figures other) => new(
            Revenue + other.Revenue,
            Volume + other.Volume,
            Credited + other.Credited,
            Invoices + other.Invoices,
            CreditNotes + other.CreditNotes);
    }

    /// <summary>A partner across every window, in the order of <see cref="Windows"/>.</summary>
    public sealed record Partner(string Code, string Name, IReadOnlyList<Figures> Years);

    /// <summary>
    /// One row of the period table: the same slice of each year's window, side by side. Its label is
    /// the asked-for window's, since the earlier windows are the same dates. <c>ShortLabel</c> is the
    /// same heading cut to fit under a chart: "Oct", "Q3".
    /// </summary>
    public sealed record Period(string Label, string ShortLabel, IReadOnlyList<decimal> Revenue, IReadOnlyList<decimal> Volume);

    private readonly IReadOnlyList<GetItemVolumeSalesReportResult> reports;
    private readonly bool readZig;

    private PartnerPerformance(
        IReadOnlyList<GetItemVolumeSalesReportResult> reports,
        bool readZig,
        IReadOnlyList<Window> windows,
        IReadOnlyList<Partner> partners)
    {
        this.reports = reports;
        this.readZig = readZig;
        Windows = windows;
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

    /// <param name="newestFirst">The asked-for window first, then each earlier year in turn.</param>
    /// <param name="readZig">Read revenue in ZiG rather than USD.</param>
    public static PartnerPerformance Build(IReadOnlyList<GetItemVolumeSalesReportResult> newestFirst, bool readZig)
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

                years[year] = years[year].Add(FiguresOf(account, readZig));
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

        return new PartnerPerformance(newestFirst, readZig, windows, partners);
    }

    /// <summary>
    /// The period rows for one partner, or for every partner together when <paramref name="cardCode"/>
    /// is null. Periods line up by position: the earlier windows are the same dates, so the n-th
    /// period of each is the same slice of its year.
    /// </summary>
    public IReadOnlyList<Period> PeriodsFor(string? cardCode)
    {
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

                    revenue[year] += readZig ? account.NetRevenueZig : account.NetRevenueUsd;
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

    private static Figures FiguresOf(ItemVolumeSalesAccountResult account, bool readZig) => new(
        readZig ? account.NetRevenueZig : account.NetRevenueUsd,
        account.NetVolume,
        readZig ? account.CreditedSalesZig : account.CreditedSalesUsd,
        account.InvoiceCount,
        account.CreditNoteCount);

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
