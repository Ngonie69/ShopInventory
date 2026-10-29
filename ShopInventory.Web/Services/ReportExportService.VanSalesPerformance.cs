using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using ShopInventory.Web.Models;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;

namespace ShopInventory.Web.Services;

/// <summary>
/// The van sales performance workbook: a dashboard of charts, then a sheet for every section of the
/// page, then the figures the charts are drawn from.
/// </summary>
/// <remarks>
/// On the Ts* ramp, matching the van attendance workbook: both are read per rep over a period, and a
/// reader moving between them should not have to re-learn where the figures sit.
///
/// Two rules every sheet keeps, both inherited from the report itself:
///
///  - Money is one numeric column per currency — "Takings USD", "Takings ZWG" — never one column
///    holding both. A column of one currency can be sorted, filtered, totalled and charted; a column
///    holding two invites a total that describes nothing. A row with nothing in a currency leaves that
///    cell blank rather than writing 0, so a SUBTOTAL skips it and nobody reads a zero as a sale.
///  - Quantity is written against its unit, and van lines carry no unit, so most rows read "unit not
///    recorded". That is the honest answer.
///
/// The charts plot the lead currency — the one that took the most — and say so in their titles. They
/// are real Excel charts, added to the saved file with the OpenXML SDK because ClosedXML cannot write
/// them, and they read the Chart Data sheet, so they redraw if a reader edits a figure there.
/// </remarks>
public partial class ReportExportService
{
    private const string VspChartDataSheet = "Chart Data";
    private const string VspMoneyFormat = "#,##0.00";
    private const string VspRateFormat = "0%";

    private static readonly XLColor VspBarColor = XLColor.FromHtml("#6F8FB3");
    private static readonly XLColor VspRateLow = XLColor.FromHtml("#FCD6B4");
    private static readonly XLColor VspRateHigh = XLColor.FromHtml("#D6E2F0");

    private const string VspNavyHex = "1B3A5C";
    private const string VspBlueHex = "2C5F8A";
    private const string VspOrangeHex = "E65100";

    public byte[] ExportVanSalesPerformanceToExcel(VanSalesPerformanceReportResponse report)
    {
        using var workbook = NewWorkbook("Van Sales Performance");
        var now = DateTime.UtcNow.AddHours(2); // CAT
        var currencies = VspCurrencies(report);
        var lead = currencies.Count > 0 ? currencies[0] : null;

        var dashboard = BuildVanPerformanceDashboardSheet(workbook, report, currencies, now);
        BuildVanPerformanceRouteSheet(workbook, report, currencies, now);
        var reps = BuildVanPerformanceRepSheet(workbook, report, currencies, now);
        BuildVanPerformanceItemSheet(workbook, report, currencies, now);
        BuildVanPerformancePriceSheet(workbook, report, now);
        BuildVanPerformanceTrendSheet(workbook, report, currencies, now);
        BuildVanPerformanceDropSheet(workbook, report, now);
        var data = BuildVanPerformanceChartDataSheet(workbook, report, lead, now);

        return AddVanPerformanceCharts(WorkbookToBytes(workbook), lead, dashboard, reps, data);
    }

    /// <summary>Every currency the period took, the summary's order first — largest first.</summary>
    private static List<string> VspCurrencies(VanSalesPerformanceReportResponse report)
    {
        var seen = report.Summary.TotalsByCurrency.Select(total => total.Currency).ToList();

        var elsewhere = report.Routes.SelectMany(route => route.TotalsByCurrency.Select(total => total.Currency))
            .Concat(report.Reps.SelectMany(rep => rep.TotalsByCurrency.Select(total => total.Currency)))
            .Concat(report.Items.SelectMany(item => item.TotalsByCurrency.Select(total => total.Currency)))
            .Concat(report.Trend.Daily.SelectMany(day => day.TotalsByCurrency.Select(total => total.Currency)));

        foreach (var currency in elsewhere)
        {
            if (!seen.Contains(currency))
            {
                seen.Add(currency);
            }
        }

        return seen;
    }

    // ── Layouts the chart pass needs ────────────────────────────────────────────

    private sealed record VspDashboardLayout(int ChartTopRow, int LastCol);

    private sealed record VspRepLayout(int ChartTopRow, int LastCol);

    /// <summary>Where each chart's figures sit on the Chart Data sheet. A block with no rows has LastRow below FirstRow.</summary>
    private sealed record VspBlock(int CategoryColumn, int FirstRow, int LastRow)
    {
        public bool HasRows => LastRow >= FirstRow;
    }

    private sealed record VspChartData(int HeaderRow, VspBlock Daily, VspBlock Routes, VspBlock Reps, VspBlock Drops, bool HasStrikeRates);

    // ── Money, per currency ─────────────────────────────────────────────────────

    private static string[] VspMoneyHeads(string noun, IReadOnlyList<string> currencies) =>
        currencies.Select(currency => $"{noun} {currency}").ToArray();

    /// <summary>
    /// One numeric cell per currency. Blank where the row took nothing in that currency, so the
    /// column's SUBTOTAL skips it and nobody reads a zero as a sale.
    /// </summary>
    private static void WriteVspMoney(
        IXLWorksheet ws, int row, int firstCol, IReadOnlyList<string> currencies,
        IEnumerable<(string Currency, decimal Gross)> totals)
    {
        var byCurrency = totals.ToDictionary(total => total.Currency, total => total.Gross);
        for (var index = 0; index < currencies.Count; index++)
        {
            var cell = ws.Cell(row, firstCol + index);
            if (byCurrency.TryGetValue(currencies[index], out var gross))
            {
                cell.Value = gross;
                cell.Style.NumberFormat.Format = VspMoneyFormat;
            }
        }
    }

    /// <summary>"Gross Profit USD", "Gross Profit ZWG", then "Margin USD", "Margin ZWG".</summary>
    private static string[] VspProfitHeads(IReadOnlyList<string> currencies) =>
        [.. VspMoneyHeads("Gross Profit", currencies), .. VspMoneyHeads("Margin", currencies)];

    /// <summary>How many columns the profit block takes: a gross profit and a margin per currency.</summary>
    private static int VspProfitWidth(bool costed, IReadOnlyList<string> currencies) =>
        costed ? currencies.Count * 2 : 0;

    /// <summary>
    /// SAP's booked gross profit and its margin, one numeric cell each per currency: the profits
    /// first, then the margins, as <see cref="VspProfitHeads"/> heads them. A currency the row took
    /// but none of whose sales is in SAP yet says so in words; a currency it did not take stays blank.
    /// </summary>
    private static void WriteVspProfit(
        IXLWorksheet ws, int row, int firstCol, IReadOnlyList<string> currencies,
        IEnumerable<(string Currency, decimal? GrossProfit, decimal? MarginPercent)> totals)
    {
        var byCurrency = totals.ToDictionary(total => total.Currency);
        for (var index = 0; index < currencies.Count; index++)
        {
            if (!byCurrency.TryGetValue(currencies[index], out var figure))
            {
                continue;
            }

            var cell = ws.Cell(row, firstCol + index);
            if (figure.GrossProfit is { } value)
            {
                cell.Value = value;
                cell.Style.NumberFormat.Format = VspMoneyFormat;
            }
            else
            {
                cell.Value = "not in SAP yet";
                cell.Style.Font.FontColor = TsTextMuted;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            }

            // The API states the margin as a percentage (20.0); the cell holds the fraction, so it
            // sorts and formats as one.
            var margin = ws.Cell(row, firstCol + currencies.Count + index);
            if (figure.MarginPercent is { } percent)
            {
                margin.Value = percent / 100m;
                margin.Style.NumberFormat.Format = "0.0%";
            }
            else
            {
                WriteVspNone(margin);
            }
        }
    }

    private static IEnumerable<(string, decimal)> VspGross(IEnumerable<VanSalesMoney> totals) =>
        totals.Select(total => (total.Currency, total.Gross));

    private static IEnumerable<(string, decimal)> VspGross(IEnumerable<VanSalesLineMoney> totals) =>
        totals.Select(total => (total.Currency, total.Gross));

    private static IEnumerable<(string, decimal?, decimal?)> VspProfit(IEnumerable<VanSalesMoney> totals) =>
        totals.Select(total => (total.Currency, total.GrossProfit, total.MarginPercent));

    private static IEnumerable<(string, decimal?, decimal?)> VspProfit(IEnumerable<VanSalesLineMoney> totals) =>
        totals.Select(total => (total.Currency, total.GrossProfit, total.MarginPercent));

    /// <summary>A rate as a real percentage; an em dash where there is no denominator. Never 0%.</summary>
    private static void WriteVspRate(IXLCell cell, double? rate)
    {
        if (rate is { } value)
        {
            cell.Value = value;
            cell.Style.NumberFormat.Format = VspRateFormat;
        }
        else
        {
            WriteVspNone(cell);
        }
    }

    private static void WriteVspCount(IXLCell cell, int? count)
    {
        if (count is { } value)
        {
            cell.Value = value;
        }
        else
        {
            WriteVspNone(cell);
        }
    }

    private static void WriteVspNone(IXLCell cell)
    {
        cell.Value = "—";
        cell.Style.Font.FontColor = TsTextMuted;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
    }

    /// <summary>
    /// A per-currency SUBTOTAL row under a single table. SUBTOTAL(109) rather than SUM so the total
    /// follows the filter.
    /// </summary>
    private static void WriteVspTotals(IXLWorksheet ws, int row, int lastCol, int firstRow, int lastRow, IEnumerable<int> columns, string label)
    {
        TsSummaryRow(ws, row, lastCol);
        ws.Cell(row, 1).Value = label;

        if (lastRow < firstRow)
        {
            return;
        }

        foreach (var column in columns)
        {
            // A column with no figure in it — gross profit none of whose sales is in SAP yet — would
            // total to 0.00, which reads as a real zero. It gets an em dash instead.
            if (!ws.Range(firstRow, column, lastRow, column).CellsUsed().Any(cell => cell.DataType == XLDataType.Number))
            {
                ws.Cell(row, column).Value = "—";
                ws.Cell(row, column).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                continue;
            }

            var letter = ToExcelColumnName(column);
            ws.Cell(row, column).FormulaA1 = $"SUBTOTAL(109,{letter}{firstRow}:{letter}{lastRow})";
            ws.Cell(row, column).Style.NumberFormat.Format = VspMoneyFormat;
        }
    }

    // ── Dashboard ───────────────────────────────────────────────────────────────

    private static VspDashboardLayout BuildVanPerformanceDashboardSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        IReadOnlyList<string> currencies,
        DateTime now)
    {
        const int lastCol = 12;
        var costed = report.Margin is { Available: true };
        var ws = workbook.Worksheets.Add("Dashboard");
        TsApplyDefaults(ws);

        var period = $"VAN SALES PERFORMANCE  —  {report.FromDate:dd MMM yyyy} to {report.ToDate:dd MMM yyyy}";
        int row = TsTitleBar(ws, period, lastCol, now);

        var summary = report.Summary;
        var lead = summary.TotalsByCurrency.FirstOrDefault();

        // One tile per currency taken, then the rest. Never one tile adding two currencies.
        var kpis = new List<(string Label, string Value, XLColor? Color)>();
        foreach (var total in summary.TotalsByCurrency)
        {
            kpis.Add(($"Takings {total.Currency}", total.Gross.ToString("N0"), null));
        }

        if (costed && lead is not null)
        {
            kpis.Add(($"Gross Profit {lead.Currency}", lead.GrossProfit?.ToString("N0") ?? "—", TsGreen));
            kpis.Add(($"Margin {lead.Currency}", lead.MarginPercent is { } margin ? $"{margin:0.0}%" : "—", null));
        }

        kpis.Add(("Productive Calls", summary.ProductiveCalls.ToString("N0"), null));
        kpis.Add(("Strike Rate", RateText(summary.StrikeRate), null));
        kpis.Add(($"Avg Drop {lead?.Currency}".Trim(), lead?.AverageDropSize is { } drop ? drop.ToString("N2") : "—", null));
        kpis.Add(("Outlets Bought", summary.CustomerCount.ToString("N0"), null));
        kpis.Add(("New Outlets", summary.NewOutlets.ToString("N0"), null));
        kpis.Add(("Items Sold", summary.ItemCount.ToString("N0"), null));
        kpis.Add(("Reps", summary.RepCount.ToString("N0"), null));
        kpis.Add(("Kilometres", summary.KilometresTravelled?.ToString("N0") ?? "—", null));

        row = TsKpiStrip(ws, row, lastCol, kpis.Take(lastCol).ToArray());

        // The caveats before the charts. A reader who has looked at a chart has already formed a view;
        // what the figures could not see has to reach them first.
        if (!report.Coverage.IsClean)
        {
            TsSectionTitle(ws, row, lastCol, "WHAT THIS PERIOD COULD NOT ANSWER");
            row += 2;

            foreach (var caveat in report.Coverage.Caveats)
            {
                ws.Cell(row, 1).Value = caveat;
                ws.Range(row, 1, row, lastCol).Merge();
                ws.Cell(row, 1).Style.Font.FontSize = 9;
                ws.Cell(row, 1).Style.Font.Italic = true;
                ws.Cell(row, 1).Style.Font.FontColor = TsOrange;
                row++;
            }

            row++;
        }

        var chartTop = 0;
        if (currencies.Count > 0 && summary.DocumentCount > 0)
        {
            var others = currencies.Count > 1
                ? $" Other currencies ({string.Join(", ", currencies.Skip(1))}) are in their own columns on every sheet."
                : string.Empty;

            TsSectionTitle(ws, row, lastCol, $"CHARTS  —  {currencies[0]}");
            row++;
            ws.Cell(row, 1).Value =
                $"Every chart plots {currencies[0]} only; currencies are never added together.{others} "
                + "The charts read the Chart Data sheet.";
            ws.Range(row, 1, row, lastCol).Merge();
            ws.Cell(row, 1).Style.Font.FontSize = 9;
            ws.Cell(row, 1).Style.Font.Italic = true;
            ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;
            row += 2;
            chartTop = row;
        }
        else
        {
            ws.Cell(row, 1).Value = "No van recorded a sale in this period, so there is nothing to chart.";
            ws.Range(row, 1, row, lastCol).Merge();
            ws.Cell(row, 1).Style.Font.Italic = true;
            ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;
        }

        TsFinalize(ws, lastCol, freezeRow: 1);

        // The KPI strip sets the widths; give the charts an even grid to sit on.
        for (var column = 1; column <= lastCol; column++)
        {
            ws.Column(column).Width = 14;
        }

        // Printed, the dashboard is one page: a chart cut across a page break reads as two charts.
        if (chartTop > 0)
        {
            ws.PageSetup.FitToPages(1, 1);
            ws.PageSetup.PrintAreas.Add(1, 1, chartTop + 2 * 18 + 1, lastCol);
        }

        return new VspDashboardLayout(chartTop, lastCol);
    }

    // ── Routes ──────────────────────────────────────────────────────────────────

    private static void BuildVanPerformanceRouteSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        IReadOnlyList<string> currencies,
        DateTime now)
    {
        var costed = report.Margin is { Available: true };
        var profitCols = VspProfitWidth(costed, currencies);
        var ws = workbook.Worksheets.Add("Routes");
        TsApplyDefaults(ws);

        // Territories
        var territoryFixed = new[] { "Territory", "Routes", "Rep-Days", "Productive Calls", "Outlets" };
        var routeFixed = new[] { "Route", "Territory", "Reps", "Rep-Days", "Planned", "Calls", "CCR", "PCR", "Km" };
        var lastCol = Math.Max(territoryFixed.Length, routeFixed.Length) + currencies.Count + profitCols;

        var period = $"TERRITORY & ROUTE  —  {report.FromDate:dd MMM yyyy} to {report.ToDate:dd MMM yyyy}";
        int row = TsTitleBar(ws, period, lastCol, now);
        row++;

        if (report.Territories.Count > 0)
        {
            var territoryCols = territoryFixed.Length + currencies.Count + profitCols;
            TsSectionTitle(ws, row, lastCol, "TERRITORIES");
            row += 2;
            row = TsColumnHeaders(ws, row, territoryCols,
                [.. territoryFixed, .. VspMoneyHeads("Takings", currencies), .. (costed ? VspProfitHeads(currencies) : [])]);

            var index = 0;
            foreach (var territory in report.Territories)
            {
                TsDataRow(ws, row, territoryCols, index % 2 == 1);
                ws.Cell(row, 1).Value = territory.DisplayTerritory;
                ws.Cell(row, 2).Value = territory.RouteCount;
                ws.Cell(row, 3).Value = territory.TradingDayCount;
                ws.Cell(row, 4).Value = territory.ProductiveCalls;
                ws.Cell(row, 5).Value = territory.CustomerCount;
                WriteVspMoney(ws, row, territoryFixed.Length + 1, currencies, VspGross(territory.TotalsByCurrency));
                if (costed)
                {
                    WriteVspProfit(ws, row, territoryFixed.Length + currencies.Count + 1, currencies, VspProfit(territory.TotalsByCurrency));
                }

                row++;
                index++;
            }

            row++;
        }

        var routeCols = routeFixed.Length + currencies.Count + profitCols;
        TsSectionTitle(ws, row, lastCol, "ROUTES");
        row += 2;
        row = TsColumnHeaders(ws, row, routeCols,
            [.. routeFixed, .. VspMoneyHeads("Takings", currencies), .. (costed ? VspProfitHeads(currencies) : [])]);

        var firstRouteRow = row;
        var routeIndex = 0;
        foreach (var route in report.Routes)
        {
            TsDataRow(ws, row, routeCols, routeIndex % 2 == 1);
            ws.Cell(row, 1).Value = route.HasRouteDay ? route.DisplayRoute : $"{route.DisplayRoute} (unrouted)";
            ws.Cell(row, 2).Value = route.Territory ?? "—";
            ws.Cell(row, 3).Value = route.RepCount;
            ws.Cell(row, 4).Value = route.TradingDayCount;
            WriteVspCount(ws.Cell(row, 5), route.PlannedCalls);
            WriteVspCount(ws.Cell(row, 6), route.Calls);
            WriteVspRate(ws.Cell(row, 7), route.CallComplianceRate);
            WriteVspRate(ws.Cell(row, 8), route.ProductiveCallRate);
            WriteVspCount(ws.Cell(row, 9), route.KilometresTravelled);
            WriteVspMoney(ws, row, routeFixed.Length + 1, currencies, VspGross(route.TotalsByCurrency));
            if (costed)
            {
                WriteVspProfit(ws, row, routeFixed.Length + currencies.Count + 1, currencies, VspProfit(route.TotalsByCurrency));
            }

            if (!route.HasRouteDay)
            {
                ws.Range(row, 1, row, routeCols).Style.Font.FontColor = TsTextMuted;
            }

            row++;
            routeIndex++;
        }

        WriteVspTotals(ws, row, routeCols, firstRouteRow, row - 1,
            Enumerable.Range(routeFixed.Length + 1, currencies.Count + (costed ? currencies.Count : 0)), "TOTAL");

        TsFinalize(ws, lastCol, freezeRow: 1, freezeCol: 1);
    }

    // ── Reps ────────────────────────────────────────────────────────────────────

    private static VspRepLayout BuildVanPerformanceRepSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        IReadOnlyList<string> currencies,
        DateTime now)
    {
        var costed = report.Margin is { Available: true };
        var fixedHeads = new[]
        {
            "Rep", "Routes", "Days", "Calls", "Bought", "Strike Rate",
            "Calls/Day", "Outlets", "New", "New Who Bought", "Items"
        };
        var lastCol = fixedHeads.Length + currencies.Count + VspProfitWidth(costed, currencies);
        var ws = workbook.Worksheets.Add("Reps");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "REP LEAGUE TABLE", lastCol, now);
        row++;
        var headerRow = row;
        row = TsColumnHeaders(ws, row, lastCol,
            [.. fixedHeads, .. VspMoneyHeads("Takings", currencies), .. (costed ? VspProfitHeads(currencies) : [])]);

        var firstRow = row;
        var index = 0;
        foreach (var rep in report.Reps)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = rep.DisplayName;
            ws.Cell(row, 2).Value = rep.Routes.Count == 0 ? "—" : string.Join(", ", rep.Routes);
            ws.Cell(row, 3).Value = rep.TradingDayCount;
            WriteVspCount(ws.Cell(row, 4), rep.Calls);
            ws.Cell(row, 5).Value = rep.ProductiveCalls;
            WriteVspRate(ws.Cell(row, 6), rep.StrikeRate);

            if (rep.CallsPerDay is { } perDay)
            {
                ws.Cell(row, 7).Value = perDay;
                ws.Cell(row, 7).Style.NumberFormat.Format = "0.0";
            }
            else
            {
                WriteVspNone(ws.Cell(row, 7));
            }

            ws.Cell(row, 8).Value = rep.CustomerCount;
            ws.Cell(row, 9).Value = rep.NewOutlets;
            ws.Cell(row, 10).Value = rep.NewOutletsWhoBought;
            ws.Cell(row, 11).Value = rep.ItemCount;
            WriteVspMoney(ws, row, fixedHeads.Length + 1, currencies, VspGross(rep.TotalsByCurrency));
            if (costed)
            {
                WriteVspProfit(ws, row, fixedHeads.Length + currencies.Count + 1, currencies, VspProfit(rep.TotalsByCurrency));
            }

            row++;
            index++;
        }

        var lastRow = row - 1;

        // Read at a glance: a bar in each takings cell, and the strike rate washed from orange (low) to
        // blue (high). Blue and orange rather than red and green, so the scale survives colour blindness.
        if (lastRow > firstRow)
        {
            for (var column = fixedHeads.Length + 1; column <= fixedHeads.Length + currencies.Count; column++)
            {
                ws.Range(firstRow, column, lastRow, column)
                    .AddConditionalFormat()
                    .DataBar(VspBarColor)
                    .LowestValue()
                    .HighestValue();
            }

            ws.Range(firstRow, 6, lastRow, 6)
                .AddConditionalFormat()
                .ColorScale()
                .LowestValue(VspRateLow)
                .HighestValue(VspRateHigh);
        }

        // One table on this sheet, so unlike the stacked sheets it can carry a filter, and the
        // SUBTOTAL row below it follows the filter.
        if (lastRow >= firstRow)
        {
            ws.Range(headerRow, 1, lastRow, lastCol).SetAutoFilter();
        }

        WriteVspTotals(ws, row, lastCol, firstRow, lastRow,
            Enumerable.Range(fixedHeads.Length + 1, currencies.Count + (costed ? currencies.Count : 0)), "TOTAL");
        row++;

        ws.Cell(row, 1).Value =
            "— means the figure cannot be measured: a rep who sold with no recorded calls has no strike rate. "
            + "The strike rate is the truck's, so both reps on a truck show the same figure.";
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;
        row += 2;

        TsFinalize(ws, lastCol, freezeRow: headerRow, freezeCol: 1);
        PadColumnsForAutoFilter(ws, lastCol);

        return new VspRepLayout(report.Reps.Count > 0 ? row : 0, lastCol);
    }

    // ── Items ───────────────────────────────────────────────────────────────────

    private static void BuildVanPerformanceItemSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        IReadOnlyList<string> currencies,
        DateTime now)
    {
        var costed = report.Margin is { Available: true };
        var fixedHeads = new[] { "#", "Item Code", "Description", "Lines", "Outlets", "Reps", "Days", "Quantity" };
        var lastCol = Math.Max(fixedHeads.Length + currencies.Count + VspProfitWidth(costed, currencies), 6 + currencies.Count);
        var ws = workbook.Worksheets.Add("Items");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "WHAT MOVED", lastCol, now);
        row++;

        TsSectionTitle(ws, row, lastCol, "RANKED ON REACH — LINES WRITTEN AND OUTLETS REACHED");
        row += 2;
        var itemCols = fixedHeads.Length + currencies.Count + VspProfitWidth(costed, currencies);
        row = TsColumnHeaders(ws, row, itemCols,
            [.. fixedHeads, .. VspMoneyHeads("Value", currencies), .. (costed ? VspProfitHeads(currencies) : [])]);

        var index = 0;
        foreach (var item in report.Items)
        {
            TsDataRow(ws, row, itemCols, index % 2 == 1);
            ws.Cell(row, 1).Value = item.Rank;
            ws.Cell(row, 2).Value = item.ItemCode;
            ws.Cell(row, 3).Value = item.ItemDescription ?? "—";
            ws.Cell(row, 4).Value = item.LineCount;
            ws.Cell(row, 5).Value = item.CustomerCount;
            ws.Cell(row, 6).Value = item.RepCount;
            ws.Cell(row, 7).Value = item.TradingDayCount;
            ws.Cell(row, 8).Value = QuantityText(item.QuantitiesByUoM);
            WriteVspMoney(ws, row, fixedHeads.Length + 1, currencies, VspGross(item.TotalsByCurrency));
            if (costed)
            {
                WriteVspProfit(ws, row, fixedHeads.Length + currencies.Count + 1, currencies, VspProfit(item.TotalsByCurrency));
            }

            row++;
            index++;
        }

        row++;
        TsSectionTitle(ws, row, lastCol, "STOPPED SELLING — SOLD IN THE PRECEDING PERIOD, NOT IN THIS ONE");
        row += 2;
        var lapsedCols = 6 + currencies.Count;
        row = TsColumnHeaders(ws, row, lapsedCols,
            ["Item Code", "Description", "Last Sold", "Days Ago", "Lines Before", "Outlets Before", .. VspMoneyHeads("Value Before", currencies)]);

        var lapsedIndex = 0;
        foreach (var item in report.LapsedItems)
        {
            TsDataRow(ws, row, lapsedCols, lapsedIndex % 2 == 1);
            ws.Cell(row, 1).Value = item.ItemCode;
            ws.Cell(row, 2).Value = item.ItemDescription ?? "—";
            WriteVanPerformanceDate(ws.Cell(row, 3), item.LastSoldOn);
            ws.Cell(row, 4).Value = item.DaysSinceLastSale;
            ws.Cell(row, 5).Value = item.PriorLineCount;
            ws.Cell(row, 6).Value = item.PriorCustomerCount;
            WriteVspMoney(ws, row, 7, currencies, VspGross(item.PriorTotalsByCurrency));
            row++;
            lapsedIndex++;
        }

        TsFinalize(ws, lastCol, freezeRow: 1, freezeCol: 1);
    }

    // ── Price realisation ───────────────────────────────────────────────────────

    private static void BuildVanPerformancePriceSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        DateTime now)
    {
        const int lastCol = 9;
        var ws = workbook.Worksheets.Add("Price Realisation");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "PRICE REALISATION", lastCol, now);

        // Said on the sheet, not only on the page. A workbook is forwarded, and whoever opens it
        // second will not have read the page it came from.
        ws.Cell(row, 1).Value =
            "Peer-relative. Each rep's achieved price against what the same item, unit and currency "
            + "fetched across everybody. Not measured against a list price — there is no trustworthy "
            + "local one. Discounts are not reported because neither van path records one: every van "
            + "line reads 0%.";
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;
        ws.Cell(row, 1).Style.Alignment.WrapText = true;
        ws.Row(row).Height = 28;
        row += 2;

        row = TsColumnHeaders(ws, row, lastCol,
            ["Item Code", "Description", "Currency", "Unit", "Lines", "Average", "Lowest", "Highest", "Spread %"]);

        var index = 0;
        foreach (var price in report.ItemPrices)
        {
            TsDataRow(ws, row, lastCol, index % 2 == 1);
            ws.Cell(row, 1).Value = price.ItemCode;
            ws.Cell(row, 2).Value = price.ItemDescription ?? "—";
            ws.Cell(row, 3).Value = price.Currency;
            ws.Cell(row, 4).Value = price.UoMCode ?? "not recorded";
            ws.Cell(row, 5).Value = price.LineCount;

            // Single item, single currency, single unit: these are safe as real numbers.
            ws.Cell(row, 6).Value = price.WeightedAveragePrice;
            ws.Cell(row, 7).Value = price.MinUnitPrice;
            ws.Cell(row, 8).Value = price.MaxUnitPrice;
            ws.Range(row, 6, row, 8).Style.NumberFormat.Format = VspMoneyFormat;

            if (price.PriceSpreadPercent is { } spread)
            {
                ws.Cell(row, 9).Value = spread;
                ws.Cell(row, 9).Style.NumberFormat.Format = "0.0";
            }
            else
            {
                WriteVspNone(ws.Cell(row, 9));
            }

            row++;
            index++;

            foreach (var rep in price.Reps)
            {
                TsDataRow(ws, row, lastCol, true);
                ws.Cell(row, 2).Value = $"   {rep.DisplayName}";
                ws.Cell(row, 2).Style.Font.Italic = true;
                ws.Cell(row, 5).Value = rep.LineCount;
                ws.Cell(row, 6).Value = rep.WeightedAveragePrice;
                ws.Cell(row, 6).Style.NumberFormat.Format = VspMoneyFormat;

                if (rep.VarianceFromItemPercent is { } variance)
                {
                    ws.Cell(row, 9).Value = variance;
                    ws.Cell(row, 9).Style.NumberFormat.Format = "0.0";
                    if (variance < 0)
                    {
                        ws.Cell(row, 9).Style.Font.FontColor = TsOrange;
                    }
                }
                else
                {
                    WriteVspNone(ws.Cell(row, 9));
                }

                row++;
            }
        }

        TsFinalize(ws, lastCol, freezeRow: 1, freezeCol: 1);
    }

    // ── Trend ───────────────────────────────────────────────────────────────────

    private static void BuildVanPerformanceTrendSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        IReadOnlyList<string> currencies,
        DateTime now)
    {
        var costed = report.Margin is { Available: true };
        var money = currencies.Count + VspProfitWidth(costed, currencies);
        var lastCol = 6 + money;
        var ws = workbook.Worksheets.Add("Trend");
        TsApplyDefaults(ws);

        string[] Money() => [.. VspMoneyHeads("Takings", currencies), .. (costed ? VspProfitHeads(currencies) : [])];

        void WriteMoney(int row, int firstCol, List<VanSalesMoney> totals)
        {
            WriteVspMoney(ws, row, firstCol, currencies, VspGross(totals));
            if (costed)
            {
                WriteVspProfit(ws, row, firstCol + currencies.Count, currencies, VspProfit(totals));
            }
        }

        int row = TsTitleBar(ws, "TREND", lastCol, now);
        row++;

        TsSectionTitle(ws, row, lastCol, "BY DAY OF WEEK — AVERAGES DIVIDE BY DAYS IN PERIOD, NOT DAYS TRADED");
        row += 2;
        row = TsColumnHeaders(ws, row, 6 + money, ["Day", "In Period", "Traded", "Documents", "Per Day", "Productive Calls", .. Money()]);

        var index = 0;
        foreach (var point in report.Trend.DayOfWeek)
        {
            TsDataRow(ws, row, 6 + money, index % 2 == 1);
            ws.Cell(row, 1).Value = point.Label;
            ws.Cell(row, 2).Value = point.CalendarDayCount;
            ws.Cell(row, 3).Value = point.ActiveDayCount;
            ws.Cell(row, 4).Value = point.DocumentCount;
            if (point.DocumentsPerCalendarDay is { } perDay)
            {
                ws.Cell(row, 5).Value = perDay;
                ws.Cell(row, 5).Style.NumberFormat.Format = "0.0";
            }
            else
            {
                WriteVspNone(ws.Cell(row, 5));
            }

            ws.Cell(row, 6).Value = point.ProductiveCalls;
            WriteMoney(row, 7, point.TotalsByCurrency);
            row++;
            index++;
        }

        row++;
        TsSectionTitle(ws, row, lastCol, "BY MONTH");
        row += 2;
        row = TsColumnHeaders(ws, row, 6 + money, ["Month", "Part Month", "Days", "Traded", "Documents", "Productive Calls", .. Money()]);

        var monthIndex = 0;
        foreach (var point in report.Trend.Monthly)
        {
            TsDataRow(ws, row, 6 + money, monthIndex % 2 == 1);
            ws.Cell(row, 1).Value = point.Label;
            ws.Cell(row, 2).Value = point.IsPartial ? "yes" : "";
            ws.Cell(row, 3).Value = point.CalendarDayCount;
            ws.Cell(row, 4).Value = point.ActiveDayCount;
            ws.Cell(row, 5).Value = point.DocumentCount;
            ws.Cell(row, 6).Value = point.ProductiveCalls;
            WriteMoney(row, 7, point.TotalsByCurrency);
            row++;
            monthIndex++;
        }

        row++;
        TsSectionTitle(ws, row, lastCol, "DAY BY DAY — SILENT DAYS INCLUDED");
        row += 2;
        row = TsColumnHeaders(ws, row, 5 + money, ["Date", "Day", "Reps Out", "Documents", "Productive Calls", .. Money()]);

        var dayIndex = 0;
        foreach (var point in report.Trend.Daily)
        {
            TsDataRow(ws, row, 5 + money, dayIndex % 2 == 1);
            WriteVanPerformanceDate(ws.Cell(row, 1), point.TradingDate);
            ws.Cell(row, 2).Value = point.DayOfWeek.ToString();
            ws.Cell(row, 3).Value = point.RepsTrading;
            ws.Cell(row, 4).Value = point.DocumentCount;
            ws.Cell(row, 5).Value = point.ProductiveCalls;
            WriteMoney(row, 6, point.TotalsByCurrency);

            if (point.DocumentCount == 0)
            {
                ws.Range(row, 1, row, 5 + money).Style.Font.FontColor = TsTextMuted;
            }

            row++;
            dayIndex++;
        }

        TsFinalize(ws, lastCol, freezeRow: 1, freezeCol: 1);
    }

    // ── Drop size ───────────────────────────────────────────────────────────────

    private static void BuildVanPerformanceDropSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        DateTime now)
    {
        const int lastCol = 6;
        var ws = workbook.Worksheets.Add("Drop Size");
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, "DROP SIZE DISTRIBUTION", lastCol, now);

        ws.Cell(row, 1).Value =
            "A drop is one shop, on one day, in one currency — two invoices at the same counter "
            + "are one drop. A median well under the mean means a few large accounts are carrying a "
            + "long tail of calls that barely pay for the stop.";
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;
        ws.Cell(row, 1).Style.Alignment.WrapText = true;
        ws.Row(row).Height = 24;
        row += 2;

        foreach (var distribution in report.DropSizes)
        {
            TsSectionTitle(ws, row, lastCol, distribution.Currency.ToUpperInvariant());
            row += 2;

            row = TsKpiStrip(ws, row, lastCol,
                ("Drops", distribution.DropCount.ToString("N0"), null),
                ("Mean", distribution.Mean.ToString("N2"), null),
                ("Median", distribution.Median.ToString("N2"), null),
                ("Lower Quartile", distribution.P25.ToString("N2"), null),
                ("Upper Quartile", distribution.P75.ToString("N2"), null),
                ("Largest", distribution.Maximum.ToString("N2"), null));

            row = TsColumnHeaders(ws, row, 4, ["Band", "Drops", "Share %", $"Value {distribution.Currency}"]);

            var index = 0;
            foreach (var bucket in distribution.Buckets)
            {
                TsDataRow(ws, row, 4, index % 2 == 1);
                ws.Cell(row, 1).Value = bucket.Label;
                ws.Cell(row, 2).Value = bucket.DropCount;

                // Per currency by construction, so these are real numbers a reader may total.
                ws.Cell(row, 3).Value = bucket.SharePercent ?? 0;
                ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0.0";
                ws.Cell(row, 4).Value = bucket.Total;
                ws.Cell(row, 4).Style.NumberFormat.Format = VspMoneyFormat;
                row++;
                index++;
            }

            row++;
        }

        TsFinalize(ws, lastCol, freezeRow: 1);
    }

    // ── Chart data ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The figures the charts read, in the lead currency, one block per chart. Bars are written
    /// smallest first because a bar chart draws its first category at the bottom, and the largest
    /// belongs at the top.
    /// </summary>
    private static VspChartData BuildVanPerformanceChartDataSheet(
        XLWorkbook workbook,
        VanSalesPerformanceReportResponse report,
        string? lead,
        DateTime now)
    {
        const int lastCol = 13;
        var ws = workbook.Worksheets.Add(VspChartDataSheet);
        TsApplyDefaults(ws);

        int row = TsTitleBar(ws, $"CHART DATA{(lead is null ? "" : $"  —  {lead}")}", lastCol, now);
        ws.Cell(row, 1).Value = lead is null
            ? "Nothing sold in this period, so there is nothing to chart."
            : $"What the Dashboard and Reps charts are drawn from: {lead} only. A blank is a day, route or "
              + "rep with nothing to plot, not a zero.";
        ws.Range(row, 1, row, lastCol).Merge();
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.Italic = true;
        ws.Cell(row, 1).Style.Font.FontColor = TsTextMuted;

        const int headerRow = 4;
        var firstRow = headerRow + 1;
        var empty = new VspBlock(1, firstRow, firstRow - 1);

        if (lead is null)
        {
            TsFinalize(ws, lastCol, freezeRow: 1);
            return new VspChartData(headerRow, empty, empty with { CategoryColumn = 5 }, empty with { CategoryColumn = 8 }, empty with { CategoryColumn = 12 }, false);
        }

        void Header(int column, params string[] heads)
        {
            var range = ws.Range(headerRow, column, headerRow, column + heads.Length - 1);
            range.Style.Fill.BackgroundColor = TsHeaderBg;
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Font.Bold = true;
            range.Style.Font.FontSize = 9;
            for (var index = 0; index < heads.Length; index++)
            {
                ws.Cell(headerRow, column + index).Value = heads[index];
            }
        }

        static decimal? Gross(IEnumerable<VanSalesMoney> totals, string currency) =>
            totals.FirstOrDefault(total => total.Currency == currency)?.Gross;

        // Daily: A date label, B takings, C productive calls. A silent day leaves both blank, so the
        // line breaks there rather than diving to zero and back.
        Header(1, "Day", $"Takings {lead}", "Productive Calls");
        row = firstRow;
        foreach (var day in report.Trend.Daily)
        {
            ws.Cell(row, 1).Value = day.TradingDate.ToString("ddd dd MMM");
            if (Gross(day.TotalsByCurrency, lead) is { } gross)
            {
                ws.Cell(row, 2).Value = gross;
                ws.Cell(row, 2).Style.NumberFormat.Format = VspMoneyFormat;
            }

            if (day.DocumentCount > 0)
            {
                ws.Cell(row, 3).Value = day.ProductiveCalls;
            }

            row++;
        }

        var daily = new VspBlock(1, firstRow, row - 1);

        // Routes: E name, F takings. Unattributed sales keep their own row, labelled as such.
        Header(5, "Route", $"Takings {lead}");
        row = firstRow;
        foreach (var route in report.Routes
                     .Select(route => (Route: route, Gross: Gross(route.TotalsByCurrency, lead)))
                     .Where(r => r.Gross is not null)
                     .OrderBy(r => r.Gross))
        {
            ws.Cell(row, 5).Value = route.Route.HasRouteDay ? route.Route.DisplayRoute : $"{route.Route.DisplayRoute} (unrouted)";
            ws.Cell(row, 6).Value = route.Gross!.Value;
            ws.Cell(row, 6).Style.NumberFormat.Format = VspMoneyFormat;
            row++;
        }

        var routes = new VspBlock(5, firstRow, row - 1);

        // Reps: H name, I takings, J strike rate — blank where it cannot be measured.
        Header(8, "Rep", $"Takings {lead}", "Strike Rate");
        row = firstRow;
        var anyRate = false;
        foreach (var rep in report.Reps
                     .Select(rep => (Rep: rep, Gross: Gross(rep.TotalsByCurrency, lead)))
                     .Where(r => r.Gross is not null)
                     .OrderBy(r => r.Gross))
        {
            ws.Cell(row, 8).Value = rep.Rep.DisplayName;
            ws.Cell(row, 9).Value = rep.Gross!.Value;
            ws.Cell(row, 9).Style.NumberFormat.Format = VspMoneyFormat;
            if (rep.Rep.StrikeRate is { } rate)
            {
                ws.Cell(row, 10).Value = rate;
                ws.Cell(row, 10).Style.NumberFormat.Format = VspRateFormat;
                anyRate = true;
            }

            row++;
        }

        var reps = new VspBlock(8, firstRow, row - 1);

        // Drops: L band, M drops, from the lead currency's own distribution.
        Header(12, $"Band {lead}", "Drops");
        row = firstRow;
        foreach (var bucket in report.DropSizes.FirstOrDefault(drops => drops.Currency == lead)?.Buckets ?? [])
        {
            ws.Cell(row, 12).Value = bucket.Label;
            ws.Cell(row, 13).Value = bucket.DropCount;
            row++;
        }

        var drops = new VspBlock(12, firstRow, row - 1);

        TsFinalize(ws, lastCol, freezeRow: headerRow);
        return new VspChartData(headerRow, daily, routes, reps, drops, anyRate);
    }

    // ── Charts ──────────────────────────────────────────────────────────────────

    private enum VspSeriesKind { Bar, Line }

    private sealed record VspSeries(int Column, string Color, VspSeriesKind Kind = VspSeriesKind.Bar, bool Labels = false);

    private sealed record VspChartSpec(
        string Title,
        VspBlock Block,
        IReadOnlyList<VspSeries> Series,
        bool Horizontal,
        string ValueFormat,
        string SecondaryFormat = "#,##0",
        double? ValueMax = null);

    /// <summary>
    /// Finishes the saved file with the OpenXML SDK: four charts on the dashboard and two under the
    /// rep league, every one reading the Chart Data sheet.
    /// </summary>
    private static byte[] AddVanPerformanceCharts(
        byte[] workbookBytes, string? lead, VspDashboardLayout dashboard, VspRepLayout reps, VspChartData data)
    {
        using var stream = new MemoryStream();
        stream.Write(workbookBytes, 0, workbookBytes.Length);
        stream.Position = 0;

        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            // ClosedXML writes an Excel 2010 data bar's id in lower case; Excel repairs that by
            // dropping the bars. The vendor workbook found it first.
            UppercaseDataBarIds(document);

            if (lead is not null && dashboard.ChartTopRow > 0)
            {
                var split = dashboard.LastCol / 2;
                var top = dashboard.ChartTopRow - 1;   // markers are zero-based
                const int height = 18;
                var axisId = 61_000_000u;

                void Place(string sheet, VspChartSpec spec, int fromCol, int fromRow, int toCol, int toRow)
                {
                    if (!spec.Block.HasRows)
                    {
                        return;
                    }

                    AddVspChart(document, sheet, spec, data.HeaderRow, axisId, fromCol, fromRow, toCol, toRow);
                    axisId += 10;
                }

                var dailyChart = new VspChartSpec(
                    $"Takings by day ({lead}) and productive calls",
                    data.Daily,
                    [new VspSeries(2, VspNavyHex), new VspSeries(3, VspOrangeHex, VspSeriesKind.Line)],
                    Horizontal: false,
                    ValueFormat: "#,##0");
                var routeChart = new VspChartSpec(
                    $"Takings by route ({lead})", data.Routes,
                    [new VspSeries(6, VspNavyHex, Labels: true)], Horizontal: true, ValueFormat: "#,##0");
                var repChart = new VspChartSpec(
                    $"Takings by rep ({lead})", data.Reps,
                    [new VspSeries(9, VspNavyHex, Labels: true)], Horizontal: true, ValueFormat: "#,##0");
                var dropChart = new VspChartSpec(
                    $"Drops by band ({lead})", data.Drops,
                    [new VspSeries(13, VspBlueHex, Labels: true)], Horizontal: false, ValueFormat: "#,##0");

                Place("Dashboard", dailyChart, 0, top, split, top + height);
                Place("Dashboard", routeChart, split, top, dashboard.LastCol, top + height);
                Place("Dashboard", repChart, 0, top + height + 1, split, top + 2 * height + 1);
                Place("Dashboard", dropChart, split, top + height + 1, dashboard.LastCol, top + 2 * height + 1);

                if (reps.ChartTopRow > 0)
                {
                    var repTop = reps.ChartTopRow - 1;
                    var repSplit = Math.Max(4, reps.LastCol / 2);
                    Place("Reps", repChart, 0, repTop, repSplit, repTop + height);

                    if (data.HasStrikeRates)
                    {
                        var strikeChart = new VspChartSpec(
                            "Strike rate by rep", data.Reps,
                            [new VspSeries(10, VspOrangeHex, Labels: true)], Horizontal: true,
                            ValueFormat: "0%", ValueMax: null);
                        Place("Reps", strikeChart, repSplit, repTop, reps.LastCol, repTop + height);
                    }
                }
            }
        }

        return stream.ToArray();
    }

    private static void AddVspChart(
        SpreadsheetDocument document, string targetSheet, VspChartSpec spec, int headerRow, uint axisId,
        int fromCol, int fromRow, int toCol, int toRow)
    {
        var workbookPart = document.WorkbookPart;
        if (workbookPart is null || GetWorksheetPartByName(workbookPart, targetSheet) is not { } target)
        {
            return;
        }

        var drawingsPart = EnsureDrawingsPart(target);
        var chartPart = drawingsPart.AddNewPart<ChartPart>();
        chartPart.ChartSpace = BuildVspChartSpace(spec, headerRow, axisId);
        chartPart.ChartSpace.Save();

        AppendChartAnchor(drawingsPart, chartPart, spec.Title, fromCol, fromRow, toCol, toRow);
    }

    private static C.ChartSpace BuildVspChartSpace(VspChartSpec spec, int headerRow, uint axisId)
    {
        var block = spec.Block;
        var categories = BuildSheetRangeFormula(VspChartDataSheet, block.FirstRow, block.CategoryColumn, block.LastRow, block.CategoryColumn);

        var chartSpace = new C.ChartSpace();
        chartSpace.Append(new C.EditingLanguage { Val = "en-US" });
        chartSpace.Append(new C.RoundedCorners { Val = false });

        var chart = new C.Chart();
        chart.Append(VspTitle(spec.Title));
        chart.Append(new C.AutoTitleDeleted { Val = false });

        var plotArea = new C.PlotArea(new C.Layout());

        var bars = spec.Series.Where(series => series.Kind == VspSeriesKind.Bar).ToList();
        var lines = spec.Series.Where(series => series.Kind == VspSeriesKind.Line).ToList();

        var catAxis = axisId;
        var valAxis = axisId + 1;
        var catAxis2 = axisId + 2;
        var valAxis2 = axisId + 3;

        var barChart = new C.BarChart();
        barChart.Append(new C.BarDirection { Val = spec.Horizontal ? C.BarDirectionValues.Bar : C.BarDirectionValues.Column });
        barChart.Append(new C.BarGrouping { Val = C.BarGroupingValues.Clustered });
        barChart.Append(new C.VaryColors { Val = false });

        uint index = 0;
        foreach (var series in bars)
        {
            var bar = new C.BarChartSeries();
            bar.Append(new C.Index { Val = index });
            bar.Append(new C.Order { Val = index });
            bar.Append(VspSeriesText(headerRow, series.Column));
            bar.Append(new C.ChartShapeProperties(
                new A.SolidFill(new A.RgbColorModelHex { Val = series.Color }),
                new A.Outline(new A.NoFill())));
            bar.Append(new C.InvertIfNegative { Val = false });
            if (series.Labels)
            {
                bar.Append(VspDataLabels(spec.ValueFormat, spec.Horizontal ? C.DataLabelPositionValues.OutsideEnd : C.DataLabelPositionValues.OutsideEnd));
            }

            bar.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categories))));
            bar.Append(new C.Values(new C.NumberReference(new C.Formula(
                BuildSheetRangeFormula(VspChartDataSheet, block.FirstRow, series.Column, block.LastRow, series.Column)))));
            barChart.Append(bar);
            index++;
        }

        barChart.Append(new C.GapWidth { Val = (ushort)(spec.Horizontal ? 45 : 35) });
        barChart.Append(new C.AxisId { Val = catAxis });
        barChart.Append(new C.AxisId { Val = valAxis });
        plotArea.Append(barChart);

        if (lines.Count > 0)
        {
            var lineChart = new C.LineChart();
            lineChart.Append(new C.Grouping { Val = C.GroupingValues.Standard });
            lineChart.Append(new C.VaryColors { Val = false });

            foreach (var series in lines)
            {
                var line = new C.LineChartSeries();
                line.Append(new C.Index { Val = index });
                line.Append(new C.Order { Val = index });
                line.Append(VspSeriesText(headerRow, series.Column));
                line.Append(new C.ChartShapeProperties(
                    new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = series.Color })) { Width = 22225 }));
                line.Append(new C.Marker(
                    new C.Symbol { Val = C.MarkerStyleValues.Circle },
                    new C.Size { Val = 5 },
                    new C.ChartShapeProperties(
                        new A.SolidFill(new A.RgbColorModelHex { Val = series.Color }),
                        new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = series.Color })))));
                line.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categories))));
                line.Append(new C.Values(new C.NumberReference(new C.Formula(
                    BuildSheetRangeFormula(VspChartDataSheet, block.FirstRow, series.Column, block.LastRow, series.Column)))));
                line.Append(new C.Smooth { Val = false });
                lineChart.Append(line);
                index++;
            }

            lineChart.Append(new C.ShowMarker { Val = true });
            lineChart.Append(new C.AxisId { Val = catAxis2 });
            lineChart.Append(new C.AxisId { Val = valAxis2 });
            plotArea.Append(lineChart);
        }

        plotArea.Append(VspCategoryAxis(catAxis, valAxis, spec.Horizontal ? C.AxisPositionValues.Left : C.AxisPositionValues.Bottom,
            deleted: false, rotate: !spec.Horizontal && block.LastRow - block.FirstRow > 10));
        plotArea.Append(VspValueAxis(valAxis, catAxis, spec.Horizontal ? C.AxisPositionValues.Bottom : C.AxisPositionValues.Left,
            spec.ValueFormat, gridlines: true, crossesMax: false, max: spec.ValueMax));

        if (lines.Count > 0)
        {
            plotArea.Append(VspCategoryAxis(catAxis2, valAxis2, C.AxisPositionValues.Bottom, deleted: true, rotate: false));
            plotArea.Append(VspValueAxis(valAxis2, catAxis2, C.AxisPositionValues.Right,
                spec.SecondaryFormat, gridlines: false, crossesMax: true, max: null));
        }

        chart.Append(plotArea);

        if (spec.Series.Count > 1)
        {
            chart.Append(new C.Legend(
                new C.LegendPosition { Val = C.LegendPositionValues.Bottom },
                new C.Overlay { Val = false },
                VspTextProperties(900)));
        }

        chart.Append(new C.PlotVisibleOnly { Val = true });
        chart.Append(new C.DisplayBlanksAs { Val = C.DisplayBlanksAsValues.Gap });
        chartSpace.Append(chart);
        chartSpace.Append(VspTextProperties(900));

        return chartSpace;
    }

    private static C.Title VspTitle(string text) =>
        new(
            new C.ChartText(new C.RichText(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(
                    new A.ParagraphProperties(new A.DefaultRunProperties { FontSize = 1200, Bold = false }),
                    new A.Run(
                        new A.RunProperties { Language = "en-US", FontSize = 1200, Bold = false },
                        new A.Text(text))))),
            new C.Overlay { Val = false });

    private static C.SeriesText VspSeriesText(int headerRow, int column) =>
        new(new C.StringReference(new C.Formula(BuildSheetCellFormula(VspChartDataSheet, headerRow, column))));

    private static C.TextProperties VspTextProperties(int size) =>
        new(
            new A.BodyProperties(),
            new A.ListStyle(),
            new A.Paragraph(new A.ParagraphProperties(new A.DefaultRunProperties { FontSize = size })));

    private static C.DataLabels VspDataLabels(string format, C.DataLabelPositionValues position) =>
        new(
            new C.NumberingFormat { FormatCode = format, SourceLinked = false },
            VspTextProperties(800),
            new C.DataLabelPosition { Val = position },
            new C.ShowLegendKey { Val = false },
            new C.ShowValue { Val = true },
            new C.ShowCategoryName { Val = false },
            new C.ShowSeriesName { Val = false },
            new C.ShowPercent { Val = false },
            new C.ShowBubbleSize { Val = false });

    private static C.CategoryAxis VspCategoryAxis(uint id, uint crossing, C.AxisPositionValues position, bool deleted, bool rotate)
    {
        var axis = new C.CategoryAxis();
        axis.Append(new C.AxisId { Val = id });
        axis.Append(new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }));
        axis.Append(new C.Delete { Val = deleted });
        axis.Append(new C.AxisPosition { Val = position });
        axis.Append(new C.NumberingFormat { FormatCode = "General", SourceLinked = true });
        axis.Append(new C.MajorTickMark { Val = C.TickMarkValues.None });
        axis.Append(new C.MinorTickMark { Val = C.TickMarkValues.None });
        axis.Append(new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo });
        axis.Append(new C.TextProperties(
            rotate
                ? new A.BodyProperties { Rotation = -2700000, Vertical = A.TextVerticalValues.Horizontal }
                : new A.BodyProperties(),
            new A.ListStyle(),
            new A.Paragraph(new A.ParagraphProperties(new A.DefaultRunProperties { FontSize = 800 }))));
        axis.Append(new C.CrossingAxis { Val = crossing });
        axis.Append(new C.Crosses { Val = C.CrossesValues.AutoZero });
        axis.Append(new C.AutoLabeled { Val = true });
        axis.Append(new C.LabelAlignment { Val = C.LabelAlignmentValues.Center });
        axis.Append(new C.LabelOffset { Val = 100 });
        axis.Append(new C.NoMultiLevelLabels { Val = true });
        return axis;
    }

    private static C.ValueAxis VspValueAxis(
        uint id, uint crossing, C.AxisPositionValues position, string format, bool gridlines, bool crossesMax, double? max)
    {
        var scaling = new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax });
        if (max is { } top)
        {
            scaling.Append(new C.MaxAxisValue { Val = top });
        }

        scaling.Append(new C.MinAxisValue { Val = 0 });

        var axis = new C.ValueAxis();
        axis.Append(new C.AxisId { Val = id });
        axis.Append(scaling);
        axis.Append(new C.Delete { Val = false });
        axis.Append(new C.AxisPosition { Val = position });
        if (gridlines)
        {
            axis.Append(new C.MajorGridlines(new C.ChartShapeProperties(
                new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "D9D9D9" })))));
        }

        axis.Append(new C.NumberingFormat { FormatCode = format, SourceLinked = false });
        axis.Append(new C.MajorTickMark { Val = C.TickMarkValues.None });
        axis.Append(new C.MinorTickMark { Val = C.TickMarkValues.None });
        axis.Append(new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo });
        axis.Append(VspTextProperties(800));
        axis.Append(new C.CrossingAxis { Val = crossing });
        axis.Append(crossesMax
            ? new C.Crosses { Val = C.CrossesValues.Maximum }
            : new C.Crosses { Val = C.CrossesValues.AutoZero });
        axis.Append(new C.CrossBetween { Val = C.CrossBetweenValues.Between });
        return axis;
    }
}
