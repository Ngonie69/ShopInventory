using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The ADR performance workbook: what the ADRs ordered, followed to what the vans actually sold.
/// </summary>
/// <remarks>
/// <para>Eight sheets, opening on Summary: the headline against the period before, the order-to-sale
/// stages, where the gap went, who needs attention and who raised the order book, with two charts.
/// Behind it, one table per sheet — ADR League, Daily, Gap by ADR, Shops, Items Fill and Order Ledger —
/// each with an autofilter, then Notes.</para>
/// <para>Unlike the older van workbooks, money here is a number in a column of its own currency, never
/// text: every row names its currency (or every column does), so a reader can filter, sum and pivot
/// without ever adding ZiG to USD. Rates are formulas over those numbers so a filtered total still
/// reads true. Drawn from the "ADR Performance Redesign" design canvas (2026-10-29).</para>
/// </remarks>
public partial class ReportExportService
{
    private const string AdrMoneyFormat = "#,##0.00";
    private const string AdrRateFormat = "0.0%";
    private const string AdrQuantityFormat = "#,##0.00";

    private static readonly XLColor AdrWarn = XLColor.FromHtml("#E65100");
    private static readonly XLColor AdrGood = XLColor.FromHtml("#2E7D32");

    private byte[] ExportAdrConversionWorkbook(AdrPerformanceReportResponse report, AdrConversionReport conversion)
    {
        using var workbook = NewWorkbook("ADR Performance");
        var now = DateTime.UtcNow.AddHours(2); // CAT
        var currencies = conversion.Overall.Money.Select(money => money.Currency).ToList();
        var lead = AdrConversionReading.LeadCurrency(conversion);
        var names = report.Adrs.ToDictionary(adr => adr.UserId);

        var summary = BuildAdrSummarySheet(workbook, report, conversion, lead, currencies, names, now);
        var league = BuildAdrConversionLeagueSheet(workbook, report, conversion, currencies, names, now);
        BuildAdrDailySheet(workbook, conversion, names, now);
        BuildAdrGapSheet(workbook, conversion, currencies, names, now);
        BuildAdrConversionShopsSheet(workbook, conversion, lead, names, now);
        BuildAdrItemsFillSheet(workbook, conversion, names, now);
        BuildAdrLedgerSheet(workbook, conversion, names, now);
        BuildAdrNotesSheet(workbook, report, conversion, now);

        workbook.Worksheet("Summary").SetTabActive();

        return AddAdrCharts(WorkbookToBytes(workbook), summary, league, lead);
    }

    // ── Summary ─────────────────────────────────────────────────────────────────

    /// <summary>Where the two chart sources sit and where the charts go, for the post-processing pass.</summary>
    private sealed record AdrSummaryLayout(int LastCol, int DaysHeaderRow, int DaysFirstRow, int DaysLastRow);

    private sealed record AdrLeagueLayout(int HeaderRow, int FirstRow, int LastRow, int OrderedColumn, int InvoicedColumn);

    private static AdrSummaryLayout BuildAdrSummarySheet(
        XLWorkbook workbook,
        AdrPerformanceReportResponse report,
        AdrConversionReport conversion,
        string? lead,
        List<string> currencies,
        Dictionary<Guid, AdrPerformanceRow> names,
        DateTime now)
    {
        var lastCol = Math.Max(7, 2 + currencies.Count * 2);
        var ws = workbook.Worksheets.Add("Summary");
        TsApplyDefaults(ws);

        var row = TsTitleBar(ws, "ADR PERFORMANCE — ORDER TO ACTUAL SALE", lastCol, now);
        var scope = report.Adrs.Count == 1 && report.Overall.ActiveAdrCount > 1 ? report.Adrs[0].DisplayName : "all ADRs";
        AdrNote(ws, row++, lastCol,
            $"Period {report.FromDate:dd MMM yyyy} to {report.ToDate:dd MMM yyyy} · {scope}"
            + (conversion.Previous is { } previous ? $" · compared with {previous.FromDate:dd MMM} to {previous.ToDate:dd MMM yyyy}" : string.Empty)
            + " · money per currency, never added across currencies");

        // The caveats reach a forwarded workbook before any figure does.
        if (report.Caveats.Count > 0)
        {
            row++;
            TsSectionTitle(ws, row++, lastCol, "WHAT THIS PERIOD COULD NOT ANSWER");
            foreach (var caveat in report.Caveats)
            {
                AdrNote(ws, row++, lastCol, $"• {caveat}", italic: false, colour: AdrWarn);
            }
        }

        var focus = report.Adrs.Count == 1 && conversion.Adrs.Count == 1 ? conversion.Adrs[0].Summary : conversion.Overall;
        var before = conversion.Previous is { } prior
            ? report.Adrs.Count == 1 && conversion.Adrs.Count == 1
                ? prior.Adrs.FirstOrDefault(adr => adr.UserId == conversion.Adrs[0].UserId)?.Summary
                : prior.Overall
            : null;
        var money = focus.In(lead);
        var was = before?.In(lead);

        // ── Headline ──
        row++;
        TsSectionTitle(ws, row++, lastCol, $"HEADLINE ({lead ?? "no orders"})");
        row = TsColumnHeaders(ws, row, 4, ["Measure", "This period", "Period before", "Change"]);
        var headline = new (string Measure, object? Now, object? Before, string Format, bool HigherIsBetter)[]
        {
            ("Actual sales from orders", money?.Invoiced, was?.Invoiced, AdrMoneyFormat, true),
            ("Ordered", money?.Ordered, was?.Ordered, AdrMoneyFormat, true),
            ("Order-to-sale conversion", money?.Conversion, was?.Conversion, AdrRateFormat, true),
            ("Fill rate (value delivered)", money?.FillRate, was?.FillRate, AdrRateFormat, true),
            ("Median days order to invoice", focus.MedianDaysToInvoice, before?.MedianDaysToInvoice, "0.0", false),
            ("Invoiced by the next day", focus.InvoicedByNextDay, before?.InvoicedByNextDay, AdrRateFormat, true),
            ("Strike rate", focus.StrikeRate, before?.StrikeRate, AdrRateFormat, true),
            ("Share of the serving vans' sales", money?.VanShare, was?.VanShare, AdrRateFormat, true),
            ("Gross profit % on SAP cost", money?.MarginPercent is { } gp ? (double)gp / 100 : null, null, AdrRateFormat, true)
        };

        var index = 0;
        foreach (var (measure, current, previousValue, format, higherIsBetter) in headline)
        {
            TsDataRow(ws, row, 4, index++ % 2 == 1);
            ws.Cell(row, 1).Value = measure;
            AdrNumber(ws.Cell(row, 2), current, format);
            AdrNumber(ws.Cell(row, 3), previousValue, format);
            ws.Cell(row, 2).Style.Font.Bold = true;

            if (current is not null && previousValue is not null)
            {
                var change = ws.Cell(row, 4);
                // Money changes by a percentage; a rate changes by points, and saying "+3.2%" of a rate
                // would read as a relative change.
                (change.FormulaA1, change.Style.NumberFormat.Format) = format switch
                {
                    AdrMoneyFormat => ($"=IF(C{row}=0,\"\",B{row}/C{row}-1)", "+0.0%;-0.0%;0.0%"),
                    AdrRateFormat => ($"=(B{row}-C{row})*100", "+0.0\" pts\";-0.0\" pts\";0.0\" pts\""),
                    _ => ($"=B{row}-C{row}", "+0.0;-0.0;0.0")
                };
                var better = Convert.ToDouble(current) >= Convert.ToDouble(previousValue);
                change.Style.Font.FontColor = better == higherIsBetter ? AdrGood : TsRed;
            }

            row++;
        }

        // ── Order to sale ──
        row++;
        TsSectionTitle(ws, row++, lastCol, "ORDER TO SALE");
        var stageHeaders = new List<string> { "Stage", "Orders" };
        foreach (var currency in currencies)
        {
            stageHeaders.Add(currency);
            stageHeaders.Add($"{currency} % of ordered");
        }

        var stageLast = stageHeaders.Count;
        row = TsColumnHeaders(ws, row, stageLast, stageHeaders.ToArray());
        var orderedRow = row;
        var counts = focus.Counts;
        var stages = new (string Stage, int Orders, Func<AdrConversionMoney, decimal> Value)[]
        {
            ("Ordered", counts.Ordered, part => part.Ordered),
            ("Reached SAP", counts.ReachedSap, part => part.ReachedSap),
            ("Invoiced — actual sales", counts.Invoiced, part => part.Invoiced),
            ("Never became a sale", counts.Ordered - counts.Invoiced, part => part.Gap)
        };

        index = 0;
        foreach (var (stage, orders, value) in stages)
        {
            TsDataRow(ws, row, stageLast, index++ % 2 == 1);
            ws.Cell(row, 1).Value = stage;
            ws.Cell(row, 2).Value = orders;

            for (var c = 0; c < currencies.Count; c++)
            {
                var part = focus.In(currencies[c]);
                var valueColumn = 3 + c * 2;
                AdrNumber(ws.Cell(row, valueColumn), part is null ? null : value(part), AdrMoneyFormat);
                var letter = XLHelper.GetColumnLetterFromNumber(valueColumn);
                ws.Cell(row, valueColumn + 1).FormulaA1 = $"=IF({letter}{orderedRow}=0,\"\",{letter}{row}/{letter}{orderedRow})";
                ws.Cell(row, valueColumn + 1).Style.NumberFormat.Format = AdrRateFormat;
            }

            if (stage.StartsWith("Invoiced", StringComparison.Ordinal))
            {
                ws.Range(row, 1, row, stageLast).Style.Font.Bold = true;
            }

            row++;
        }

        // ── Where the gap went ──
        row++;
        TsSectionTitle(ws, row++, lastCol, "WHERE THE GAP WENT");
        var gapHeaders = new List<string> { "Reason", "Orders affected" };
        foreach (var currency in currencies)
        {
            gapHeaders.Add(currency);
            gapHeaders.Add($"{currency} % of gap");
        }

        gapHeaders.Add("Look on sheet");
        var gapLast = gapHeaders.Count;
        row = TsColumnHeaders(ws, row, gapLast, gapHeaders.ToArray());
        var gapFirst = row;
        var sheetFor = new[] { "Items Fill", "Order Ledger", "Order Ledger", "Order Ledger", "Order Ledger" };
        var reasons = AdrConversionReading.GapReasons(focus, null);
        var gapLastRow = gapFirst + reasons.Count - 1;

        for (var r = 0; r < reasons.Count; r++)
        {
            TsDataRow(ws, row, gapLast, r % 2 == 1);
            ws.Cell(row, 1).Value = reasons[r].Reason;
            ws.Cell(row, 2).Value = reasons[r].Orders;

            for (var c = 0; c < currencies.Count; c++)
            {
                var part = focus.In(currencies[c]);
                var value = part is null ? (decimal?)null : AdrConversionReading.GapReasons(focus, part)[r].Value;
                var valueColumn = 3 + c * 2;
                AdrNumber(ws.Cell(row, valueColumn), value, AdrMoneyFormat);
                var letter = XLHelper.GetColumnLetterFromNumber(valueColumn);
                ws.Cell(row, valueColumn + 1).FormulaA1 =
                    $"=IF(SUM({letter}{gapFirst}:{letter}{gapLastRow})=0,\"\",{letter}{row}/SUM({letter}{gapFirst}:{letter}{gapLastRow}))";
                ws.Cell(row, valueColumn + 1).Style.NumberFormat.Format = AdrRateFormat;
            }

            ws.Cell(row, gapLast).Value = sheetFor[r];
            ws.Cell(row, gapLast).Style.Font.FontColor = TsHeaderBg;
            row++;
        }

        TsDataRow(ws, row, gapLast, false);
        StyleTotalsRowTs(ws, row, gapLast);
        ws.Cell(row, 1).Value = "Total gap";
        ws.Cell(row, 2).FormulaA1 = $"=SUM(B{gapFirst}:B{gapLastRow})";
        for (var c = 0; c < currencies.Count; c++)
        {
            var letter = XLHelper.GetColumnLetterFromNumber(3 + c * 2);
            ws.Cell(row, 3 + c * 2).FormulaA1 = $"=SUM({letter}{gapFirst}:{letter}{gapLastRow})";
            ws.Cell(row, 3 + c * 2).Style.NumberFormat.Format = AdrMoneyFormat;
        }

        row += 2;

        // ── Needs attention ──
        var attention = conversion.Adrs
            .Select(adr => (Adr: adr, Money: adr.Summary.In(lead)))
            .Where(pair => pair.Money is { Conversion: < 0.75 } or { FillRate: < 0.85 })
            .OrderBy(pair => pair.Money?.Conversion ?? 1)
            .ToList();

        if (attention.Count > 0)
        {
            TsSectionTitle(ws, row++, lastCol, $"NEEDS ATTENTION ({lead})");
            row = TsColumnHeaders(ws, row, 7, ["ADR", "Orders", "Invoiced", "Conversion", "Fill rate", "Days to invoice", "Why flagged"]);
            index = 0;
            foreach (var (adr, part) in attention)
            {
                TsDataRow(ws, row, 7, index++ % 2 == 1);
                ws.Cell(row, 1).Value = AdrName(names, adr.UserId);
                ws.Cell(row, 2).Value = adr.Summary.Counts.Ordered;
                AdrNumber(ws.Cell(row, 3), part?.Invoiced, AdrMoneyFormat);
                AdrNumber(ws.Cell(row, 4), part?.Conversion, AdrRateFormat);
                AdrNumber(ws.Cell(row, 5), part?.FillRate, AdrRateFormat);
                AdrNumber(ws.Cell(row, 6), adr.Summary.MedianDaysToInvoice, "0.0");
                ws.Cell(row, 7).Value = string.Join(" and ", new[]
                {
                    part is { Conversion: < 0.75 } ? "conversion under 75%" : null,
                    part is { FillRate: < 0.85 } ? "fill under 85%" : null
                }.Where(reason => reason is not null));
                if (part is { Conversion: < 0.75 }) ws.Cell(row, 4).Style.Font.FontColor = AdrWarn;
                if (part is { FillRate: < 0.85 }) ws.Cell(row, 5).Style.Font.FontColor = AdrWarn;
                row++;
            }

            row++;
        }

        // ── Who raised the order book ──
        TsSectionTitle(ws, row++, lastCol, "WHO RAISED THE ORDER BOOK");
        row = TsColumnHeaders(ws, row, 4, ["Raised by", "Orders", "Value", "Share"]);
        index = 0;
        foreach (var channel in report.OrdersByChannel)
        {
            TsDataRow(ws, row, 4, index++ % 2 == 1);
            ws.Cell(row, 1).Value = channel.Channel;
            ws.Cell(row, 2).Value = channel.OrderCount;
            ws.Cell(row, 3).Value = channel.Shares.Count == 0
                ? "—"
                : string.Join(", ", channel.Shares.Select(share => $"{share.Currency} {share.Gross:N2}"));
            ws.Cell(row, 4).Value = channel.Shares.Count == 0
                ? "nothing raised"
                : string.Join(", ", channel.Shares.Select(share => share.Share is { } s ? $"{s.ToString("P0")} of {share.Currency}" : $"— of {share.Currency}"));
            if (channel.IsAdr) ws.Range(row, 1, row, 4).Style.Font.Bold = true;
            row++;
        }

        // ── Day by day, the chart's source ──
        row++;
        TsSectionTitle(ws, row++, lastCol, $"DAY BY DAY ({lead})");
        var daysHeader = row;
        row = TsColumnHeaders(ws, row, 4, ["Day", "Ordered", "Invoiced", "Conversion"]);
        var daysFirst = row;
        var days = conversion.Days
            .GroupBy(day => day.TradingDate.Date)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var parts = group.SelectMany(day => day.Money)
                    .Where(part => string.Equals(part.Currency, lead, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return (Date: group.Key, Ordered: parts.Sum(part => part.Ordered), Invoiced: parts.Sum(part => part.Invoiced));
            })
            .Where(day => day.Ordered > 0 || day.Invoiced > 0)
            .ToList();

        index = 0;
        foreach (var day in days)
        {
            TsDataRow(ws, row, 4, index++ % 2 == 1);
            ws.Cell(row, 1).Value = day.Date.ToString("ddd dd MMM");
            AdrNumber(ws.Cell(row, 2), day.Ordered, AdrMoneyFormat);
            AdrNumber(ws.Cell(row, 3), day.Invoiced, AdrMoneyFormat);
            ws.Cell(row, 4).FormulaA1 = $"=IF(B{row}=0,\"\",C{row}/B{row})";
            ws.Cell(row, 4).Style.NumberFormat.Format = AdrRateFormat;
            row++;
        }

        TsFinalize(ws, lastCol);
        ws.Column(1).Width = Math.Max(ws.Column(1).Width, 30);

        return new AdrSummaryLayout(lastCol, daysHeader, daysFirst, days.Count == 0 ? daysFirst - 1 : row - 1);
    }

    // ── ADR League ──────────────────────────────────────────────────────────────

    private static AdrLeagueLayout BuildAdrConversionLeagueSheet(
        XLWorkbook workbook,
        AdrPerformanceReportResponse report,
        AdrConversionReport conversion,
        List<string> currencies,
        Dictionary<Guid, AdrPerformanceRow> names,
        DateTime now)
    {
        var ws = workbook.Worksheets.Add("ADR League");
        TsApplyDefaults(ws);

        var headers = new List<string> { "#", "ADR", "Route", "Van", "Days active", "Calls", "Productive", "Strike %", "Orders", "Invoiced orders", "Short orders", "Cancelled" };
        var fixedColumns = headers.Count;
        foreach (var currency in currencies)
        {
            headers.AddRange([$"{currency} Ordered", $"{currency} Invoiced", $"{currency} Conv. %", $"{currency} Short", $"{currency} Fill %", $"{currency} Van sales", $"{currency} Share %"]);
        }

        headers.AddRange(["Median days to invoice", "Invoiced next day %", "GP %"]);
        var lastCol = headers.Count;

        var row = TsTitleBar(ws, "ADR LEAGUE — RANKED ON ACTUAL SALES", lastCol, now);
        var headerRow = row;
        row = TsColumnHeaders(ws, row, lastCol, headers.ToArray());
        var firstRow = row;
        var lead = currencies.FirstOrDefault();

        var ranked = conversion.Adrs
            .OrderByDescending(adr => adr.Summary.In(lead)?.Invoiced ?? 0m)
            .ThenByDescending(adr => adr.Summary.In(lead)?.Ordered ?? 0m)
            .ToList();

        var index = 0;
        foreach (var adr in ranked)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            var person = names.GetValueOrDefault(adr.UserId);
            var summary = adr.Summary;
            ws.Cell(row, 1).Value = ++index;
            ws.Cell(row, 2).Value = person?.DisplayName ?? "Unknown ADR";
            ws.Cell(row, 3).Value = adr.RouteName is null ? "—" : $"{adr.RouteCode} {adr.RouteName}";
            ws.Cell(row, 4).Value = person?.VanAccountCode ?? "—";
            ws.Cell(row, 5).Value = person?.ActiveDays ?? 0;
            AdrNumber(ws.Cell(row, 6), summary.Calls, "#,##0");
            ws.Cell(row, 7).Value = summary.ProductiveCalls;
            ws.Cell(row, 8).FormulaA1 = $"=IF(N(F{row})=0,\"\",G{row}/F{row})";
            ws.Cell(row, 8).Style.NumberFormat.Format = AdrRateFormat;
            ws.Cell(row, 9).Value = summary.Counts.Ordered;
            ws.Cell(row, 10).Value = summary.Counts.Invoiced;
            ws.Cell(row, 11).Value = summary.Counts.ShortSupplied;
            ws.Cell(row, 12).Value = summary.Counts.Cancelled;

            for (var c = 0; c < currencies.Count; c++)
            {
                var part = summary.In(currencies[c]);
                var col = fixedColumns + 1 + c * 7;
                WriteAdrCurrencyBlock(ws, row, col, part);
            }

            var tail = fixedColumns + currencies.Count * 7 + 1;
            AdrNumber(ws.Cell(row, tail), summary.MedianDaysToInvoice, "0.0");
            AdrNumber(ws.Cell(row, tail + 1), summary.InvoicedByNextDay, AdrRateFormat);
            AdrNumber(ws.Cell(row, tail + 2), summary.In(lead)?.MarginPercent is { } gp ? (double)gp / 100 : null, AdrRateFormat);
            row++;
        }

        var lastRow = row - 1;

        // The totals follow the filter: SUBTOTAL for counts and money, and the rates recomputed from them.
        TsDataRow(ws, row, lastCol, false);
        StyleTotalsRowTs(ws, row, lastCol);
        ws.Cell(row, 2).Value = $"All ADRs · {report.Overall.ActiveAdrCount:N0} active of {report.Overall.AdrCount:N0}";
        if (lastRow >= firstRow)
        {
            foreach (var col in new[] { 6, 7, 9, 10, 11, 12 })
            {
                AdrSubtotal(ws, row, col, firstRow, lastRow, "#,##0");
            }

            ws.Cell(row, 8).FormulaA1 = $"=IF(F{row}=0,\"\",G{row}/F{row})";
            ws.Cell(row, 8).Style.NumberFormat.Format = AdrRateFormat;

            for (var c = 0; c < currencies.Count; c++)
            {
                var col = fixedColumns + 1 + c * 7;
                foreach (var money in new[] { col, col + 1, col + 3, col + 5 })
                {
                    AdrSubtotal(ws, row, money, firstRow, lastRow, AdrMoneyFormat);
                }

                AdrRateFormulas(ws, row, col);
            }
        }

        AdrFinishTable(ws, headerRow, lastRow, lastCol, freezeCol: 2);

        var leadColumn = fixedColumns + 1;
        return new AdrLeagueLayout(headerRow, firstRow, lastRow, leadColumn, leadColumn + 1);
    }

    /// <summary>Ordered, invoiced, conversion, short, fill, van sales and share: one currency's seven columns.</summary>
    private static void WriteAdrCurrencyBlock(IXLWorksheet ws, int row, int col, AdrConversionMoney? part)
    {
        if (part is null)
        {
            return;
        }

        AdrNumber(ws.Cell(row, col), part.Ordered, AdrMoneyFormat);
        AdrNumber(ws.Cell(row, col + 1), part.Invoiced, AdrMoneyFormat);
        AdrNumber(ws.Cell(row, col + 3), part.ShortSupplied, AdrMoneyFormat);
        AdrNumber(ws.Cell(row, col + 5), part.VanSales, AdrMoneyFormat);
        AdrRateFormulas(ws, row, col);
        AdrNumber(ws.Cell(row, col + 4), part.FillRate, AdrRateFormat);
        AdrNumber(ws.Cell(row, col + 6), part.VanShare, AdrRateFormat);

        ws.Cell(row, col + 1).Style.Font.Bold = true;
        if (part.Conversion is < 0.75) ws.Cell(row, col + 2).Style.Font.FontColor = AdrWarn;
        if (part.FillRate is < 0.85) ws.Cell(row, col + 4).Style.Font.FontColor = AdrWarn;
    }

    /// <summary>
    /// A currency block's conversion, as a formula over its own ordered and invoiced cells so a filtered
    /// total still reads true. The fill and the share rest on figures the sheet does not show, so a row
    /// carries them as values and the totals row leaves them to the Summary.
    /// </summary>
    private static void AdrRateFormulas(IXLWorksheet ws, int row, int col)
    {
        var ordered = XLHelper.GetColumnLetterFromNumber(col);
        var invoiced = XLHelper.GetColumnLetterFromNumber(col + 1);
        ws.Cell(row, col + 2).FormulaA1 = $"=IF(N({ordered}{row})=0,\"\",{invoiced}{row}/{ordered}{row})";
        ws.Cell(row, col + 2).Style.NumberFormat.Format = AdrRateFormat;
    }

    // ── Daily ───────────────────────────────────────────────────────────────────

    private static void BuildAdrDailySheet(
        XLWorkbook workbook,
        AdrConversionReport conversion,
        Dictionary<Guid, AdrPerformanceRow> names,
        DateTime now)
    {
        const int lastCol = 12;
        var ws = workbook.Worksheets.Add("Daily");
        TsApplyDefaults(ws);

        var row = TsTitleBar(ws, "DAILY — ONE ROW PER ADR PER TRADING DAY", lastCol, now);
        var headerRow = row;
        row = TsColumnHeaders(ws, row, lastCol,
            ["Date", "Weekday", "ADR", "Calls", "Productive", "Orders", "Currency", "Ordered", "Invoiced", "Conv. %", "Short", "Strike %"]);
        var firstRow = row;
        var index = 0;

        foreach (var day in conversion.Days.OrderBy(day => day.TradingDate).ThenBy(day => AdrName(names, day.UserId)))
        {
            // One row per currency; the calls go on the first only, so a pivot does not count them twice.
            var parts = day.Money.Count == 0 ? [null] : day.Money.Cast<AdrConversionDayMoney?>().ToList();
            var first = true;

            foreach (var part in parts)
            {
                TsDataRow(ws, row, lastCol, index++ % 2 == 1);
                WriteVanPerformanceDate(ws.Cell(row, 1), day.TradingDate);
                ws.Cell(row, 2).Value = day.TradingDate.ToString("ddd");
                ws.Cell(row, 3).Value = AdrName(names, day.UserId);

                if (first)
                {
                    AdrNumber(ws.Cell(row, 4), day.Calls, "#,##0");
                    ws.Cell(row, 5).Value = day.ProductiveCalls;
                    ws.Cell(row, 6).Value = day.Orders;
                    ws.Cell(row, 12).FormulaA1 = $"=IF(N(D{row})=0,\"\",E{row}/D{row})";
                    ws.Cell(row, 12).Style.NumberFormat.Format = AdrRateFormat;
                }

                if (part is not null)
                {
                    ws.Cell(row, 7).Value = part.Currency;
                    AdrNumber(ws.Cell(row, 8), part.Ordered, AdrMoneyFormat);
                    AdrNumber(ws.Cell(row, 9), part.Invoiced, AdrMoneyFormat);
                    ws.Cell(row, 10).FormulaA1 = $"=IF(N(H{row})=0,\"\",I{row}/H{row})";
                    ws.Cell(row, 10).Style.NumberFormat.Format = AdrRateFormat;
                    AdrNumber(ws.Cell(row, 11), part.ShortSupplied, AdrMoneyFormat);
                    if (part.Conversion is < 0.75) ws.Cell(row, 10).Style.Font.FontColor = AdrWarn;
                }

                first = false;
                row++;
            }
        }

        AdrFinishTable(ws, headerRow, row - 1, lastCol, freezeCol: 3);
    }

    // ── Gap by ADR ──────────────────────────────────────────────────────────────

    private static void BuildAdrGapSheet(
        XLWorkbook workbook,
        AdrConversionReport conversion,
        List<string> currencies,
        Dictionary<Guid, AdrPerformanceRow> names,
        DateTime now)
    {
        var headers = new List<string> { "ADR" };
        foreach (var currency in currencies)
        {
            headers.AddRange([$"{currency} Short-supplied", $"{currency} No linked invoice", $"{currency} Cancelled",
                $"{currency} Not yet in SAP", $"{currency} Repriced", $"{currency} Total gap", $"{currency} % of ordered"]);
        }

        var lastCol = Math.Max(headers.Count, 2);
        var ws = workbook.Worksheets.Add("Gap by ADR");
        TsApplyDefaults(ws);

        var row = TsTitleBar(ws, "WHERE EACH ADR'S GAP WENT", lastCol, now);
        var headerRow = row;
        row = TsColumnHeaders(ws, row, lastCol, headers.ToArray());
        var firstRow = row;
        var index = 0;

        foreach (var adr in conversion.Adrs.OrderBy(adr => AdrName(names, adr.UserId), StringComparer.OrdinalIgnoreCase))
        {
            TsDataRow(ws, row, lastCol, index++ % 2 == 1);
            ws.Cell(row, 1).Value = AdrName(names, adr.UserId);

            for (var c = 0; c < currencies.Count; c++)
            {
                var part = adr.Summary.In(currencies[c]);
                var col = 2 + c * 7;
                if (part is null) continue;

                AdrNumber(ws.Cell(row, col), part.ShortSupplied, AdrMoneyFormat);
                AdrNumber(ws.Cell(row, col + 1), part.NoLinkedInvoice, AdrMoneyFormat);
                AdrNumber(ws.Cell(row, col + 2), part.Cancelled, AdrMoneyFormat);
                AdrNumber(ws.Cell(row, col + 3), part.NotYetInSap, AdrMoneyFormat);
                AdrNumber(ws.Cell(row, col + 4), part.Repriced, AdrMoneyFormat);
                var from = XLHelper.GetColumnLetterFromNumber(col);
                var to = XLHelper.GetColumnLetterFromNumber(col + 4);
                ws.Cell(row, col + 5).FormulaA1 = $"=SUM({from}{row}:{to}{row})";
                ws.Cell(row, col + 5).Style.NumberFormat.Format = AdrMoneyFormat;
                ws.Cell(row, col + 5).Style.Font.Bold = true;
                AdrNumber(ws.Cell(row, col + 6), part.Ordered > 0 ? (double)(part.Gap / part.Ordered) : null, AdrRateFormat);

                // The biggest reason on the row, so whose gap is supply and whose is process reads at a glance.
                var reasons = new[] { part.ShortSupplied, part.NoLinkedInvoice, part.Cancelled, part.NotYetInSap, part.Repriced };
                var biggest = Array.IndexOf(reasons, reasons.Max());
                if (reasons[biggest] > 0) ws.Cell(row, col + biggest).Style.Font.FontColor = AdrWarn;
            }

            row++;
        }

        var lastRow = row - 1;
        TsDataRow(ws, row, lastCol, false);
        StyleTotalsRowTs(ws, row, lastCol);
        ws.Cell(row, 1).Value = "All ADRs";
        if (lastRow >= firstRow)
        {
            for (var c = 0; c < currencies.Count; c++)
            {
                for (var k = 0; k < 6; k++)
                {
                    AdrSubtotal(ws, row, 2 + c * 7 + k, firstRow, lastRow, AdrMoneyFormat);
                }
            }
        }

        AdrFinishTable(ws, headerRow, lastRow, lastCol, freezeCol: 1);
    }

    // ── Shops ───────────────────────────────────────────────────────────────────

    private static void BuildAdrConversionShopsSheet(
        XLWorkbook workbook,
        AdrConversionReport conversion,
        string? lead,
        Dictionary<Guid, AdrPerformanceRow> names,
        DateTime now)
    {
        const int lastCol = 16;
        var ws = workbook.Worksheets.Add("Shops");
        TsApplyDefaults(ws);

        var row = TsTitleBar(ws, "SHOPS — DID THEIR ORDERS ARRIVE?", lastCol, now);
        var headerRow = row;
        row = TsColumnHeaders(ws, row, lastCol,
            ["ADR", "Shop code", "Shop", "Visits", "Orders", "Invoiced orders", "Short orders", "Uninvoiced orders",
             "Currency", "Ordered", "Invoiced", "Conv. %", "Short", "Last order", "Days since", "Flag"]);
        var index = 0;

        foreach (var shop in conversion.Shops)
        {
            var parts = shop.Money.Count == 0 ? [null] : shop.Money.Cast<AdrConversionDayMoney?>().ToList();
            foreach (var part in parts)
            {
                TsDataRow(ws, row, lastCol, index++ % 2 == 1);
                ws.Cell(row, 1).Value = AdrName(names, shop.UserId);
                ws.Cell(row, 2).Value = shop.CustomerCode;
                ws.Cell(row, 3).Value = shop.CustomerName ?? "—";
                ws.Cell(row, 4).Value = shop.Visits;
                ws.Cell(row, 5).Value = shop.Orders;
                ws.Cell(row, 6).Value = shop.Invoiced;
                ws.Cell(row, 7).Value = shop.ShortSupplied;
                ws.Cell(row, 8).Value = shop.Uninvoiced;

                if (part is not null)
                {
                    ws.Cell(row, 9).Value = part.Currency;
                    AdrNumber(ws.Cell(row, 10), part.Ordered, AdrMoneyFormat);
                    AdrNumber(ws.Cell(row, 11), part.Invoiced, AdrMoneyFormat);
                    ws.Cell(row, 12).FormulaA1 = $"=IF(N(J{row})=0,\"\",K{row}/J{row})";
                    ws.Cell(row, 12).Style.NumberFormat.Format = AdrRateFormat;
                    AdrNumber(ws.Cell(row, 13), part.ShortSupplied, AdrMoneyFormat);
                    if (part.Conversion is < 0.7) ws.Cell(row, 12).Style.Font.FontColor = AdrWarn;
                }

                if (shop.LastOrderOn is { } last)
                {
                    WriteVanPerformanceDate(ws.Cell(row, 14), last);
                    ws.Cell(row, 15).FormulaA1 = $"=TODAY()-N{row}";
                    ws.Cell(row, 15).Style.NumberFormat.Format = "0";
                }

                if (AdrConversionReading.ShopFlag(shop, lead) is { } flag)
                {
                    ws.Cell(row, 16).Value = flag;
                    ws.Cell(row, 16).Style.Font.FontColor = AdrWarn;
                    ws.Cell(row, 16).Style.Font.Bold = true;
                }

                row++;
            }
        }

        AdrFinishTable(ws, headerRow, row - 1, lastCol, freezeCol: 3);
    }

    // ── Items Fill ──────────────────────────────────────────────────────────────

    private static void BuildAdrItemsFillSheet(
        XLWorkbook workbook,
        AdrConversionReport conversion,
        Dictionary<Guid, AdrPerformanceRow> names,
        DateTime now)
    {
        const int lastCol = 14;
        var ws = workbook.Worksheets.Add("Items Fill");
        TsApplyDefaults(ws);

        var row = TsTitleBar(ws, "ITEMS — ORDERED AGAINST SUPPLIED", lastCol, now);
        var headerRow = row;
        row = TsColumnHeaders(ws, row, lastCol,
            ["ADR", "Item code", "Description", "UoM", "Lines", "Shops", "Ordered qty", "Supplied qty", "Short qty",
             "Fill %", "Shops let down", "Currency", "Ordered value", "Short value"]);
        var index = 0;

        foreach (var item in conversion.Items)
        {
            var parts = item.Money.Count == 0 ? [null] : item.Money.Cast<AdrConversionItemMoney?>().ToList();
            var first = true;
            foreach (var part in parts)
            {
                TsDataRow(ws, row, lastCol, index++ % 2 == 1);
                ws.Cell(row, 1).Value = AdrName(names, item.UserId);
                ws.Cell(row, 2).Value = item.ItemCode;
                ws.Cell(row, 3).Value = item.ItemDescription ?? "—";
                ws.Cell(row, 4).Value = item.UoMCode ?? "—";

                // The quantities belong to the item, not to a currency: written once.
                if (first)
                {
                    ws.Cell(row, 5).Value = item.LineCount;
                    ws.Cell(row, 6).Value = item.ShopCount;
                    AdrNumber(ws.Cell(row, 7), item.OrderedQuantity, AdrQuantityFormat);
                    AdrNumber(ws.Cell(row, 8), item.SuppliedQuantity, AdrQuantityFormat);
                    AdrNumber(ws.Cell(row, 9), item.ShortQuantity, AdrQuantityFormat);
                    ws.Cell(row, 10).FormulaA1 = $"=IF(N(G{row})=0,\"\",1-I{row}/G{row})";
                    ws.Cell(row, 10).Style.NumberFormat.Format = AdrRateFormat;
                    ws.Cell(row, 11).Value = item.ShortShopCount;
                    if (item.FillRate is < 0.85) ws.Cell(row, 10).Style.Font.FontColor = AdrWarn;
                }

                if (part is not null)
                {
                    ws.Cell(row, 12).Value = part.Currency;
                    AdrNumber(ws.Cell(row, 13), part.Ordered, AdrMoneyFormat);
                    AdrNumber(ws.Cell(row, 14), part.ShortSupplied, AdrMoneyFormat);
                    if (part.ShortSupplied > 0) ws.Cell(row, 14).Style.Font.FontColor = AdrWarn;
                }

                first = false;
                row++;
            }
        }

        AdrFinishTable(ws, headerRow, row - 1, lastCol, freezeCol: 3);
    }

    // ── Order Ledger ────────────────────────────────────────────────────────────

    private static void BuildAdrLedgerSheet(
        XLWorkbook workbook,
        AdrConversionReport conversion,
        Dictionary<Guid, AdrPerformanceRow> names,
        DateTime now)
    {
        const int lastCol = 19;
        var ws = workbook.Worksheets.Add("Order Ledger");
        TsApplyDefaults(ws);

        var row = TsTitleBar(ws, "ORDER LEDGER — EVERY ORDER BESIDE ITS INVOICE", lastCol, now);
        var headerRow = row;
        row = TsColumnHeaders(ws, row, lastCol,
            ["Order", "Captured", "Trading day", "ADR", "Shop code", "Shop", "Currency", "Lines", "Ordered",
             "SAP order", "Invoice reference", "Invoice no.", "Invoiced on", "Invoiced", "Days to invoice",
             "Short", "Repriced", "Variance", "Outcome"]);
        var index = 0;

        foreach (var order in conversion.Orders)
        {
            TsDataRow(ws, row, lastCol, index++ % 2 == 1);
            ws.Cell(row, 1).Value = order.OrderNumber;
            ws.Cell(row, 2).Value = order.CapturedAtCat;
            ws.Cell(row, 2).Style.NumberFormat.Format = "dd/mm/yyyy hh:mm";
            WriteVanPerformanceDate(ws.Cell(row, 3), order.TradingDate);
            ws.Cell(row, 4).Value = AdrName(names, order.UserId);
            ws.Cell(row, 5).Value = order.CustomerCode ?? "—";
            ws.Cell(row, 6).Value = order.CustomerName ?? "—";
            ws.Cell(row, 7).Value = order.Currency;
            ws.Cell(row, 8).Value = order.LineCount;
            AdrNumber(ws.Cell(row, 9), order.Ordered, AdrMoneyFormat);
            AdrNumber(ws.Cell(row, 10), order.SapOrderDocNum, "0");
            ws.Cell(row, 11).Value = order.InvoiceReference ?? "—";
            AdrNumber(ws.Cell(row, 12), order.InvoiceDocNum, "0");

            if (order.InvoicedAtCat is { } invoicedAt)
            {
                ws.Cell(row, 13).Value = invoicedAt;
                ws.Cell(row, 13).Style.NumberFormat.Format = "dd/mm/yyyy hh:mm";
            }

            AdrNumber(ws.Cell(row, 14), order.Invoiced, AdrMoneyFormat);
            AdrNumber(ws.Cell(row, 15), order.DaysToInvoice, "0.0");
            AdrNumber(ws.Cell(row, 16), order.Invoiced is null ? null : order.ShortSupplied, AdrMoneyFormat);
            AdrNumber(ws.Cell(row, 17), order.Invoiced is null ? null : order.Repriced, AdrMoneyFormat);
            if (order.Invoiced is not null)
            {
                ws.Cell(row, 18).FormulaA1 = $"=N{row}-I{row}";
                ws.Cell(row, 18).Style.NumberFormat.Format = AdrMoneyFormat;
                if (order.Variance is < 0) ws.Cell(row, 18).Style.Font.FontColor = AdrWarn;
            }

            ws.Cell(row, 19).Value = order.Outcome;
            ws.Cell(row, 19).Style.Font.Bold = true;
            ws.Cell(row, 19).Style.Font.FontColor = order.Outcome switch
            {
                AdrOutcome.InvoicedInFull => AdrGood,
                AdrOutcome.ShortSupplied or AdrOutcome.NoLinkedInvoice => AdrWarn,
                AdrOutcome.Cancelled => TsRed,
                _ => TsTextDark
            };

            // Kept, struck through: it was raised, and the count must match the Summary.
            if (order.Outcome == AdrOutcome.Cancelled)
            {
                ws.Range(row, 1, row, lastCol - 1).Style.Font.Strikethrough = true;
                ws.Range(row, 1, row, lastCol - 1).Style.Font.FontColor = TsTextMuted;
            }

            row++;
        }

        AdrFinishTable(ws, headerRow, row - 1, lastCol, freezeCol: 1);
    }

    // ── Notes ───────────────────────────────────────────────────────────────────

    private static void BuildAdrNotesSheet(
        XLWorkbook workbook,
        AdrPerformanceReportResponse report,
        AdrConversionReport conversion,
        DateTime now)
    {
        const int lastCol = 2;
        var ws = workbook.Worksheets.Add("Notes");
        TsApplyDefaults(ws);

        var row = TsTitleBar(ws, "NOTES — WHAT EACH FIGURE MEANS", lastCol, now);
        row = TsColumnHeaders(ws, row, lastCol, ["Term", "Meaning"]);

        var notes = new List<(string Term, string Meaning)>
        {
            ("Period", $"{report.FromDate:dd MMM yyyy} to {report.ToDate:dd MMM yyyy}, by the CAT trading day each order was captured."),
            ("Compared with", conversion.Previous is { } previous
                ? $"{previous.FromDate:dd MMM yyyy} to {previous.ToDate:dd MMM yyyy}: the same days of the month before when the period starts on the 1st, otherwise the same number of days just before it."
                : "No comparison was asked for."),
            ("Actual sales", "The van invoices raised from ADR orders, found through the invoice queue entry that names each order. Includes VAT, as the order totals do."),
            ("Ordered", "Every order captured in the period, cancelled ones included: they are part of what was lost. An order captured without prices takes its value from its invoice."),
            ("Conversion", "Actual sales ÷ ordered, per currency."),
            ("Fill rate", "The share of the invoiced orders' value the van delivered. Short supply is valued at the order's own prices."),
            ("Short-supplied", "Invoiced with fewer units than ordered. Van sale lines carry no unit, so items are compared by item code."),
            ("No linked invoice", "In SAP or marked fulfilled, with no van invoice traced to it. An order invoiced directly in SAP B1 lands here."),
            ("Repriced", "What is left on an invoiced order once short supply is taken out; negative where the van sold more than was ordered."),
            ("Share of van sales", "Of everything the vans that served these ADRs took in the period, the part that came from ADR orders. Both are dated by the sale."),
            ("Strike rate", "Shops that ordered on a day ÷ shops checked in at on the van app that day."),
            ("Currencies", "Money is never added across currencies; each column or row names its own.")
        };

        notes.AddRange(report.Caveats.Select(caveat => ("Caveat", caveat)));

        var index = 0;
        foreach (var (term, meaning) in notes)
        {
            TsDataRow(ws, row, lastCol, index++ % 2 == 1);
            ws.Cell(row, 1).Value = term;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = meaning;
            ws.Cell(row, 2).Style.Alignment.WrapText = true;
            if (term == "Caveat") ws.Cell(row, 1).Style.Font.FontColor = AdrWarn;
            row++;
        }

        TsFinalize(ws, lastCol, freezeRow: 2);
        ws.Column(1).Width = 22;
        ws.Column(2).Width = 110;
    }

    // ── Charts ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The two Summary charts, added by reopening the saved bytes: ClosedXML cannot write charts. Each is
    /// left out when its table has nothing to draw, since a chart of zeros says nothing a blank does not.
    /// </summary>
    private static byte[] AddAdrCharts(byte[] workbookBytes, AdrSummaryLayout summary, AdrLeagueLayout league, string? lead)
    {
        var hasDays = summary.DaysLastRow >= summary.DaysFirstRow;
        var hasLeague = league.LastRow >= league.FirstRow && lead is not null;
        if (!hasDays && !hasLeague)
        {
            return workbookBytes;
        }

        using var stream = new MemoryStream();
        stream.Write(workbookBytes, 0, workbookBytes.Length);
        stream.Position = 0;

        // Anchored to the right of the tables. Markers are zero-based.
        var from = summary.LastCol + 1;
        var to = from + 10;

        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            if (hasDays)
            {
                AddExecutiveClusteredColumnChart(
                    document,
                    targetSheetName: "Summary",
                    chartName: $"Ordered against invoiced by day ({lead})",
                    sourceSheetName: "Summary",
                    headerRow: summary.DaysHeaderRow,
                    categoryColumn: 1,
                    dataStartRow: summary.DaysFirstRow,
                    dataEndRow: summary.DaysLastRow,
                    seriesColumns: [2, 3],
                    seriesColors: ["A9BCD0", "2C5F8A"],
                    fromColumn: from,
                    fromRow: 2,
                    toColumn: to,
                    toRow: 22);
            }

            if (hasLeague)
            {
                AddExecutiveClusteredColumnChart(
                    document,
                    targetSheetName: "Summary",
                    chartName: $"Ordered against invoiced by ADR ({lead})",
                    sourceSheetName: "ADR League",
                    headerRow: league.HeaderRow,
                    categoryColumn: 2,
                    dataStartRow: league.FirstRow,
                    dataEndRow: league.LastRow,
                    seriesColumns: [league.OrderedColumn, league.InvoicedColumn],
                    seriesColors: ["A9BCD0", "2C5F8A"],
                    fromColumn: from,
                    fromRow: hasDays ? 24 : 2,
                    toColumn: to,
                    toRow: hasDays ? 44 : 22);
            }
        }

        return stream.ToArray();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string AdrName(Dictionary<Guid, AdrPerformanceRow> names, Guid userId) =>
        names.TryGetValue(userId, out var person) ? person.DisplayName : "Unknown ADR";

    /// <summary>A number in its format, or an empty cell where there is no figure — never a zero standing in.</summary>
    private static void AdrNumber(IXLCell cell, object? value, string format)
    {
        switch (value)
        {
            case null:
                return;
            case decimal number:
                cell.Value = number;
                break;
            case double number:
                cell.Value = number;
                break;
            case int number:
                cell.Value = number;
                break;
            default:
                cell.Value = Convert.ToDouble(value);
                break;
        }

        cell.Style.NumberFormat.Format = format;
    }

    private static void AdrSubtotal(IXLWorksheet ws, int row, int col, int firstRow, int lastRow, string format)
    {
        var letter = XLHelper.GetColumnLetterFromNumber(col);
        ws.Cell(row, col).FormulaA1 = $"=SUBTOTAL(109,{letter}{firstRow}:{letter}{lastRow})";
        ws.Cell(row, col).Style.NumberFormat.Format = format;
    }

    private static void StyleTotalsRowTs(IXLWorksheet ws, int row, int lastCol)
    {
        var range = ws.Range(row, 1, row, lastCol);
        range.Style.Fill.BackgroundColor = TsTotalBg;
        range.Style.Font.Bold = true;
        range.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        range.Style.Border.TopBorderColor = TsGridColor;
    }

    private static void AdrNote(IXLWorksheet ws, int row, int lastCol, string text, bool italic = true, XLColor? colour = null)
    {
        ws.Range(row, 1, row, lastCol).Merge();
        var cell = ws.Cell(row, 1);
        cell.Value = text;
        cell.Style.Font.Italic = italic;
        cell.Style.Font.FontColor = colour ?? TsTextMuted;
        cell.Style.Alignment.WrapText = true;
        ws.Row(row).Height = text.Length > 140 ? 30 : 16;
    }

    /// <summary>One table per sheet, so each gets the filter the stacked van sheets cannot have.</summary>
    private static void AdrFinishTable(IXLWorksheet ws, int headerRow, int lastRow, int lastCol, int freezeCol)
    {
        if (lastRow > headerRow)
        {
            ws.Range(headerRow, 1, lastRow, lastCol).SetAutoFilter();
        }
        else
        {
            AdrNote(ws, headerRow + 1, lastCol, "Nothing in this period.");
        }

        TsFinalize(ws, lastCol, freezeRow: headerRow, freezeCol: freezeCol);
    }
}
