using System.Globalization;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using ShopInventory.Web.Features.RouteCustomers;
using ShopInventory.Web.Models;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;

namespace ShopInventory.Web.Services;

/// <summary>
/// The Route Sales Report's detail workbook: a summary with the top fives, a sheet of charts, and the full
/// tables behind them — routes, customers, every customer's items, products and the dormant list.
/// </summary>
/// <remarks>
/// <para>
/// The rankings come from <see cref="RouteSalesOverview"/>, the same code the page's overview reads, so the
/// file and the screen cannot disagree about who is first.
/// </para>
/// <para>
/// Money is one column per currency, never added across them. The charts are drawn in one currency — the one
/// the page was ranking in — because a bar of USD beside a bar of ZWG compares nothing.
/// </para>
/// <para>
/// ClosedXML cannot write a chart, so the charts are added to the saved file with the OpenXML SDK, reading
/// a "Chart data" block written beside them on the Charts sheet. They are ordinary Excel charts: they can be
/// restyled, resized or copied into a slide.
/// </para>
/// </remarks>
public partial class ReportExportService
{
    private const string RsdChartsSheet = "Charts";

    /// <summary>The first column of the Charts sheet's data blocks (S), clear of the charts in A to P.</summary>
    private const int RsdDataColumn = 19;

    /// <summary>The columns the charts are laid over on the Charts sheet.</summary>
    private const int RsdChartColumns = 16;

    /// <summary>
    /// Rows measured for column widths on the long sheets. Measuring all of a 5,000-row item sheet took
    /// most of the export's time; the first few hundred rows (and the totals) show how wide a column runs.
    /// </summary>
    private const int RouteSheetFitRows = 300;

    /// <summary>The sheets that carry data bars, and so the only ones whose bar ids need putting right.</summary>
    private static readonly string[] RsdDataBarSheets = ["Summary", "Routes", "Products"];

    /// <summary>The charts' first row, zero-based as drawing markers are: the sheet's ninth row, under the letterhead and its note.</summary>
    private const int RsdChartTop = 8;

    /// <summary>The standing-by-route chart grows with the routes, two rows a bar.</summary>
    private static int RsdStandingChartHeight(int routes) => Math.Max(16, routes * 2 + 6);

    private const string RsdValueHex = "283593";
    private const string RsdProfitHex = "43A047";
    private const string RsdActiveHex = "43A047";
    private const string RsdDormantHex = "EF6C00";
    private const string RsdNeverHex = "C62828";
    private const string RsdCustomerHex = "3949AB";
    private const string RsdProductHex = "00897B";

    public byte[] ExportRouteSalesDetailToExcel(
        RouteCustomerSalesSummaryModel summary,
        RouteCustomerProductMixModel? productMix,
        RouteCustomerProductSalesModel? productSales,
        IReadOnlyDictionary<string, string> routeLabels,
        string? currency = null)
    {
        const string title = "Route sales — detail";
        using var workbook = NewWorkbook(title);

        var currencies = RouteSalesOverview.Currencies(summary);
        var primary = currencies.FirstOrDefault(code => string.Equals(code, currency?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? currencies.FirstOrDefault();
        var salesGp = summary.Margin is { Available: true };
        var productGp = productMix?.Margin is { Available: true };

        // Tab order is the order the sheets are added in.
        WriteRsdSummarySheet(AddSheet(workbook, "Summary"), title, summary, productMix, routeLabels, currencies, primary, salesGp, productGp);
        var charts = WriteRsdChartsSheet(AddSheet(workbook, RsdChartsSheet), summary, productMix, primary, salesGp);
        WriteRsdRoutesSheet(AddSheet(workbook, "Routes"), summary, routeLabels, currencies, primary, salesGp);
        WriteRouteCustomersSheet(AddSheet(workbook, "Customers"), "Route customers", summary, routeLabels);
        WriteRsdCustomerProductsSheet(AddSheet(workbook, "Customer products"), summary, productSales, routeLabels, currencies);
        WriteRsdProductsSheet(AddSheet(workbook, "Products"), summary, productMix, currencies, productGp);
        WriteRsdDormantSheet(AddSheet(workbook, "Dormant"), summary, routeLabels);

        return AddRsdCharts(WorkbookToBytes(workbook), charts);
    }

    // ── Summary ────────────────────────────────────────────────────────────

    private static void WriteRsdSummarySheet(
        IXLWorksheet ws,
        string title,
        RouteCustomerSalesSummaryModel summary,
        RouteCustomerProductMixModel? productMix,
        IReadOnlyDictionary<string, string> routeLabels,
        IReadOnlyList<string> currencies,
        string? primary,
        bool salesGp,
        bool productGp)
    {
        var customerColumns = currencies.Select(code => RsdTopCustomerColumns(code, routeLabels, salesGp)).ToList();
        var productColumns = RsdProductColumns(currencies, productGp, withLastSold: false, RsdCosted((productMix?.Items ?? []).SelectMany(item => item.TotalsByCurrency)));
        var routeColumns = RsdRouteColumns(routeLabels, currencies, primary, salesGp, RsdCosted(summary.Routes.SelectMany(route => route.TotalsByCurrency)));
        // A money card per currency, as a number: one card holding "ZWG 2,472,539.43 / USD 223,470.75" has to
        // shrink to fit and cannot be read.
        var moneyCards = new List<(string Label, decimal Value, string Format, XLColor? Color)>();
        foreach (var code in currencies)
        {
            if (RouteSalesOverview.Total(summary, code) is { } total)
            {
                moneyCards.Add(($"Value ({code})", total.Gross, MoneyFormatFor(code), null));
            }
        }

        foreach (var code in currencies)
        {
            if (salesGp && RouteSalesOverview.Total(summary, code) is { GrossProfit: { } profit } costed)
            {
                var margin = costed.MarginPercent is { } percent ? $", {percent.ToString("0.0", CultureInfo.InvariantCulture)}%" : "";
                moneyCards.Add(($"Gross profit ({code}{margin})", profit, MoneyFormatFor(code), profit < 0 ? DangerRed : SuccessGreen));
            }
        }

        var lastCol = new[] { 4 + moneyCards.Count, productColumns.Count, routeColumns.Count }
            .Concat(customerColumns.Select(columns => columns.Count))
            .Max();

        var row = WriteReportHeader(ws, title, lastCol, summary.From, summary.To);
        var customers = summary.Routes.SelectMany(route => route.Customers).ToList();

        WriteKpiCard(ws, row, 1, "Customers", customers.Count, FormatCount);
        WriteKpiCard(ws, row, 2, "Trading", customers.Count(customer => customer.SaleCount > 0), FormatCount, SuccessGreen);
        WriteKpiCard(ws, row, 3, $"Dormant ({summary.DormantDays}+ days)",
            customers.Count(customer => RouteSalesOverview.IsDormant(customer, summary.DormantDays)), FormatCount, WarningOrange);
        WriteKpiCard(ws, row, 4, "Never bought", customers.Count(customer => customer.LastSaleAt is null), FormatCount, DangerRed);
        for (var index = 0; index < moneyCards.Count; index++)
        {
            var card = moneyCards[index];
            WriteKpiCard(ws, row, 5 + index, card.Label, card.Value, card.Format, card.Color);
        }

        row += 3;

        RsdNote(ws, row, lastCol,
            $"{summary.Routes.Count:N0} route(s), {customers.Count:N0} customer(s). Sales and value are for the dates above; " +
            "the last sale is all time. Customers are ranked by value one currency at a time; products by how often " +
            "they were bought. A blank gross profit is a sale SAP has not costed yet. The Charts sheet draws the same figures.");
        row += 2;

        var fitFrom = row;

        if (currencies.Count == 0)
        {
            WriteSectionTitle(ws, row, lastCol, $"Top {RouteSalesOverview.TopCount} customers");
            row = WriteVpTable(ws, row + 1, customerColumnsFallback(routeLabels, salesGp), Array.Empty<RouteSalesRankedCustomer>(),
                "No customer bought anything in this period.", totals: false, filter: false).NextRow + 1;
        }

        for (var index = 0; index < currencies.Count; index++)
        {
            var code = currencies[index];
            WriteSectionTitle(ws, row, lastCol, $"Top {RouteSalesOverview.TopCount} customers — {code}");
            var table = WriteVpTable(ws, row + 1, customerColumns[index], RouteSalesOverview.TopCustomers(customers, code).ToList(),
                $"No customer bought in {code} in this period.", totals: false, filter: false);
            row = table.NextRow + 1;
        }

        WriteSectionTitle(ws, row, lastCol, $"Top {RouteSalesOverview.TopCount} products — by times bought");
        var products = WriteVpTable(ws, row + 1, productColumns, RouteSalesOverview.TopProducts(productMix).ToList(),
            "Nothing was sold to route customers in this period.", totals: false, filter: false);
        row = products.NextRow + 1;

        WriteSectionTitle(ws, row, lastCol,
            primary is null ? "By route" : $"By route — largest in {primary} first");
        var routes = WriteVpTable(ws, row + 1, routeColumns, RouteSalesOverview.Routes(summary, primary).ToList(),
            "No route customers matched this report's filters.", totals: true, filter: true);

        WriteFooter(ws, routes.NextRow - 1, lastCol);
        FinalizeSheet(ws, lastCol, landscape: true, fitFromRow: fitFrom);

        static List<VpColumn<RouteSalesRankedCustomer>> customerColumnsFallback(IReadOnlyDictionary<string, string> labels, bool gp) =>
            RsdTopCustomerColumns(string.Empty, labels, gp);
    }

    private static List<VpColumn<RouteSalesRankedCustomer>> RsdTopCustomerColumns(
        string currency,
        IReadOnlyDictionary<string, string> routeLabels,
        bool salesGp)
    {
        var suffix = string.IsNullOrEmpty(currency) ? "" : $" ({currency})";
        var columns = new List<VpColumn<RouteSalesRankedCustomer>>
        {
            new("#", (cell, entry) => VpNumber(cell, entry.Rank, FormatCount)),
            new("Customer", (cell, entry) => cell.Value = entry.Customer.Name),
            new("Code", (cell, entry) => VpCode(cell, entry.Customer.Code)),
            new("Route", (cell, entry) => cell.Value = RsdRouteLabel(routeLabels, entry.Customer.AssignedBusinessPartnerCode)),
            new("Sales", (cell, entry) => VpNumber(cell, entry.Totals.SaleCount, FormatCount)),
            new($"Value{suffix}", (cell, entry) => VpNumber(cell, entry.Totals.Gross, MoneyFormatFor(currency)), Bar: true)
        };

        if (salesGp)
        {
            columns.Add(new($"Gross profit{suffix}", (cell, entry) => VpOptionalMoney(cell, entry.Totals.GrossProfit, currency)));
            columns.Add(new("Margin", (cell, entry) => RsdMargin(cell, entry.Totals.MarginPercent)));
        }

        return columns;
    }

    // ── Charts ─────────────────────────────────────────────────────────────

    /// <summary>Where a chart's figures sit on the Charts sheet: a header row and the rows under it.</summary>
    private sealed record RsdBlock(int HeaderRow, int FirstRow, int LastRow, int SeriesCount);

    private sealed record RsdChartsLayout(
        string? Currency,
        RsdBlock? RouteValue,
        RsdBlock? Standing,
        RsdBlock? TopCustomers,
        RsdBlock? TopProducts,
        RsdBlock? StandingByRoute);

    private static RsdChartsLayout WriteRsdChartsSheet(
        IXLWorksheet ws,
        RouteCustomerSalesSummaryModel summary,
        RouteCustomerProductMixModel? productMix,
        string? currency,
        bool salesGp)
    {
        var row = WriteReportHeader(ws, "Charts", RsdChartColumns, summary.From, summary.To);
        var customers = summary.Routes.SelectMany(route => route.Customers).ToList();
        var lines = RouteSalesOverview.Routes(summary, currency);

        RsdNote(ws, row, RsdChartColumns,
            (currency is null ? "No sales in this period, so only the customers are charted. " : $"Money is in {currency}. ") +
            "These are ordinary Excel charts: they read the Chart data cells to the right, and can be restyled or copied into a slide.");

        var dataRow = row;
        ws.Cell(dataRow, RsdDataColumn).Value = "Chart data";
        ws.Cell(dataRow, RsdDataColumn).Style.Font.Bold = true;
        ws.Cell(dataRow, RsdDataColumn).Style.Font.FontColor = LightNavy;
        dataRow += 2;

        RsdBlock? routeValue = null;
        if (currency is not null && lines.Any(line => line.Totals is { Gross: > 0 }))
        {
            var headers = salesGp
                ? new[] { "Route", $"Value ({currency})", $"Gross profit ({currency})" }
                : new[] { "Route", $"Value ({currency})" };
            routeValue = WriteRsdBlock(ws, ref dataRow, headers,
                lines.Select(line => (line.Route.AssignedBusinessPartnerCode, salesGp
                        ? new decimal?[] { line.Totals?.Gross ?? 0m, line.Totals?.GrossProfit }
                        : new decimal?[] { line.Totals?.Gross ?? 0m }))
                    .ToList(),
                MoneyFormatFor(currency));
        }

        RsdBlock? standing = null;
        if (customers.Count > 0)
        {
            var dormant = customers.Count(customer => RouteSalesOverview.IsDormant(customer, summary.DormantDays));
            var never = customers.Count(customer => customer.LastSaleAt is null);
            standing = WriteRsdBlock(ws, ref dataRow, ["Standing", "Customers"],
                new List<(string, decimal?[])>
                {
                    ("Active", new decimal?[] { customers.Count - dormant - never }),
                    ($"Dormant ({summary.DormantDays}+ days)", new decimal?[] { dormant }),
                    ("Never bought", new decimal?[] { never })
                },
                FormatCount);
        }

        RsdBlock? topCustomers = null;
        if (currency is not null)
        {
            var top = RouteSalesOverview.TopCustomers(customers, currency);
            if (top.Count > 0)
            {
                topCustomers = WriteRsdBlock(ws, ref dataRow, ["Customer", $"Value ({currency})"],
                    top.Select(entry => (entry.Customer.Name, new decimal?[] { entry.Totals.Gross })).ToList(),
                    MoneyFormatFor(currency));
            }
        }

        RsdBlock? topProducts = null;
        var products = RouteSalesOverview.TopProducts(productMix);
        if (products.Count > 0)
        {
            topProducts = WriteRsdBlock(ws, ref dataRow, ["Item", "Times bought"],
                products.Select(entry => (RsdItemName(entry.Item.ItemDescription, entry.Item.ItemCode), new decimal?[] { entry.Item.LineCount })).ToList(),
                FormatCount);
        }

        RsdBlock? byRoute = null;
        if (summary.Routes.Count > 0 && customers.Count > 0)
        {
            byRoute = WriteRsdBlock(ws, ref dataRow, ["Route", "Active", "Dormant", "Never bought"],
                lines.Select(line => (line.Route.AssignedBusinessPartnerCode, new decimal?[]
                    {
                        Math.Max(0, line.Route.CustomerCount - line.Route.DormantCount - line.Route.NeverBoughtCount),
                        line.Route.DormantCount,
                        line.Route.NeverBoughtCount
                    }))
                    .ToList(),
                FormatCount);
        }

        if (routeValue is null && standing is null && topCustomers is null && topProducts is null && byRoute is null)
        {
            RsdNote(ws, row + 2, RsdChartColumns, "Nothing to chart: no route customers matched this report's filters.");
        }

        FinalizeSheet(ws, RsdChartColumns, landscape: true);
        for (var column = 1; column <= RsdChartColumns; column++)
        {
            ws.Column(column).Width = 10;
        }

        ws.Column(RsdDataColumn - 1).Width = 3;
        ws.Columns(RsdDataColumn, RsdDataColumn + 3).AdjustToContents(row, Math.Max(row, dataRow));
        for (var column = RsdDataColumn; column <= RsdDataColumn + 3; column++)
        {
            ws.Column(column).Width = Math.Clamp(ws.Column(column).Width, MinColumnWidth, MaxColumnWidth);
        }

        // Printed, the sheet is the charts alone on one portrait page: the data block beside them is for
        // the charts to read, and fitting it in too split every chart across pages.
        var lastChartRow = RsdChartTop + 45 + RsdStandingChartHeight(summary.Routes.Count) + 1;
        ws.PageSetup.PrintAreas.Clear();
        ws.PageSetup.PrintAreas.Add(1, 1, lastChartRow, RsdChartColumns);
        ws.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        ws.PageSetup.FitToPages(1, 1);

        return new RsdChartsLayout(currency, routeValue, standing, topCustomers, topProducts, byRoute);
    }

    /// <summary>Writes one chart's figures at <see cref="RsdDataColumn"/> and moves <paramref name="row"/> past them.</summary>
    private static RsdBlock? WriteRsdBlock(
        IXLWorksheet ws,
        ref int row,
        IReadOnlyList<string> headers,
        IReadOnlyList<(string Category, decimal?[] Values)> rows,
        string format)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var headerRow = row;
        for (var index = 0; index < headers.Count; index++)
        {
            var cell = ws.Cell(headerRow, RsdDataColumn + index);
            cell.Value = headers[index];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontSize = 9;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = NavyBlue;
        }

        var current = headerRow + 1;
        foreach (var (category, values) in rows)
        {
            ws.Cell(current, RsdDataColumn).Value = category;
            for (var index = 0; index < values.Length; index++)
            {
                if (values[index] is { } value)
                {
                    VpNumber(ws.Cell(current, RsdDataColumn + 1 + index), value, format);
                }
            }

            current++;
        }

        var range = ws.Range(headerRow, RsdDataColumn, current - 1, RsdDataColumn + headers.Count - 1);
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = BorderGray;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        range.Style.Border.InsideBorderColor = MedGray;

        row = current + 2;
        return new RsdBlock(headerRow, headerRow + 1, current - 1, headers.Count - 1);
    }

    /// <summary>
    /// Draws the five charts onto the Charts sheet of the saved file, and puts right the data bars' rule ids
    /// ClosedXML writes in lower case (see <see cref="UppercaseDataBarIds"/>).
    /// </summary>
    private static byte[] AddRsdCharts(byte[] workbookBytes, RsdChartsLayout layout)
    {
        using var stream = new MemoryStream();
        stream.Write(workbookBytes, 0, workbookBytes.Length);
        stream.Position = 0;

        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            // Only the sheets with bars: the shared helper loads every sheet, and loading the long ones
            // cost seconds for no change.
            UppercaseDataBarIds(document, RsdDataBarSheets);

            const int top = RsdChartTop;
            const int split = 10;

            if (layout.RouteValue is { } routeValue)
            {
                var colors = new[] { RsdValueHex, RsdProfitHex };
                AddRsdChart(document, "Value and gross profit by route", (0, top, split, top + 22), part => BuildRsdBarChart(
                    part, $"Value and gross profit by route ({layout.Currency})", horizontal: true, percentStacked: false,
                    routeValue, colors, axisFormat: "#,##0", labels: false, legend: routeValue.SeriesCount > 1));
            }

            if (layout.Standing is { } standing)
            {
                AddRsdChart(document, "Customers by standing", (split, top, RsdChartColumns, top + 22), part => BuildRsdDoughnutChart(
                    part, "Customers by standing", standing, [RsdActiveHex, RsdDormantHex, RsdNeverHex]));
            }

            if (layout.TopCustomers is { } topCustomers)
            {
                AddRsdChart(document, "Top customers", (0, top + 23, 8, top + 44), part => BuildRsdBarChart(
                    part, $"Top {RouteSalesOverview.TopCount} customers by value ({layout.Currency})", horizontal: true, percentStacked: false,
                    topCustomers, [RsdCustomerHex], axisFormat: "#,##0", labels: true, legend: false));
            }

            if (layout.TopProducts is { } topProducts)
            {
                AddRsdChart(document, "Top products", (8, top + 23, RsdChartColumns, top + 44), part => BuildRsdBarChart(
                    part, $"Top {RouteSalesOverview.TopCount} products by times bought", horizontal: false, percentStacked: false,
                    topProducts, [RsdProductHex], axisFormat: "#,##0", labels: true, legend: false));
            }

            if (layout.StandingByRoute is { } byRoute)
            {
                var height = RsdStandingChartHeight(byRoute.LastRow - byRoute.FirstRow + 1);
                AddRsdChart(document, "Customer standing by route", (0, top + 45, RsdChartColumns, top + 45 + height), part => BuildRsdBarChart(
                    part, "Customer standing by route (share of customers)", horizontal: true, percentStacked: true,
                    byRoute, [RsdActiveHex, RsdDormantHex, RsdNeverHex], axisFormat: "0%", labels: false, legend: true));
            }
        }

        return stream.ToArray();
    }

    private static void AddRsdChart(
        SpreadsheetDocument document,
        string chartName,
        (int FromColumn, int FromRow, int ToColumn, int ToRow) anchor,
        Action<ChartPart> build)
    {
        if (document.WorkbookPart is not { } workbookPart || GetWorksheetPartByName(workbookPart, RsdChartsSheet) is not { } sheet)
        {
            return;
        }

        var drawings = EnsureDrawingsPart(sheet);
        var chartPart = drawings.AddNewPart<ChartPart>();
        build(chartPart);
        AppendChartAnchor(drawings, chartPart, chartName, anchor.FromColumn, anchor.FromRow, anchor.ToColumn, anchor.ToRow);
    }

    /// <summary>
    /// A bar (sideways) or column chart over one block, a series per value column. Sideways charts list
    /// their categories top-down in the block's order — Excel's default is bottom-up, which would put the
    /// largest route last.
    /// </summary>
    private static void BuildRsdBarChart(
        ChartPart chartPart,
        string title,
        bool horizontal,
        bool percentStacked,
        RsdBlock block,
        IReadOnlyList<string> colors,
        string axisFormat,
        bool labels,
        bool legend)
    {
        const uint categoryAxisId = 61_000_001;
        const uint valueAxisId = 61_000_002;

        var chartSpace = new C.ChartSpace();
        chartSpace.Append(new C.EditingLanguage { Val = "en-US" });
        chartSpace.Append(new C.RoundedCorners { Val = false });

        var chart = chartSpace.AppendChild(new C.Chart());
        chart.Append(RsdChartTitle(title));
        chart.Append(new C.AutoTitleDeleted { Val = false });

        var plotArea = chart.AppendChild(new C.PlotArea());
        plotArea.Append(new C.Layout());

        var barChart = plotArea.AppendChild(new C.BarChart());
        barChart.Append(new C.BarDirection { Val = horizontal ? C.BarDirectionValues.Bar : C.BarDirectionValues.Column });
        barChart.Append(new C.BarGrouping { Val = percentStacked ? C.BarGroupingValues.PercentStacked : C.BarGroupingValues.Clustered });
        barChart.Append(new C.VaryColors { Val = false });

        var categories = BuildSheetRangeFormula(RsdChartsSheet, block.FirstRow, RsdDataColumn, block.LastRow, RsdDataColumn);
        for (var index = 0; index < block.SeriesCount; index++)
        {
            var column = RsdDataColumn + 1 + index;
            var series = new C.BarChartSeries(
                new C.Index { Val = (uint)index },
                new C.Order { Val = (uint)index },
                new C.SeriesText(new C.StringReference(new C.Formula(BuildSheetCellFormula(RsdChartsSheet, block.HeaderRow, column)))),
                new C.ChartShapeProperties(
                    new A.SolidFill(new A.RgbColorModelHex { Val = colors[index % colors.Count] }),
                    new A.Outline(new A.NoFill())),
                new C.InvertIfNegative { Val = false });
            series.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(categories))));
            series.Append(new C.Values(new C.NumberReference(new C.Formula(
                BuildSheetRangeFormula(RsdChartsSheet, block.FirstRow, column, block.LastRow, column)))));
            barChart.Append(series);
        }

        if (labels)
        {
            barChart.Append(new C.DataLabels(
                new C.NumberingFormat { FormatCode = axisFormat, SourceLinked = false },
                RsdChartText(900),
                new C.DataLabelPosition { Val = C.DataLabelPositionValues.OutsideEnd },
                new C.ShowLegendKey { Val = false },
                new C.ShowValue { Val = true },
                new C.ShowCategoryName { Val = false },
                new C.ShowSeriesName { Val = false },
                new C.ShowPercent { Val = false },
                new C.ShowBubbleSize { Val = false }));
        }

        barChart.Append(new C.GapWidth { Val = (ushort)(percentStacked ? 50 : 70) });
        if (percentStacked)
        {
            // A stacked chart's series sit on top of one another only with a full overlap.
            barChart.Append(new C.Overlap { Val = 100 });
        }

        barChart.Append(new C.AxisId { Val = categoryAxisId });
        barChart.Append(new C.AxisId { Val = valueAxisId });

        plotArea.Append(new C.CategoryAxis(
            new C.AxisId { Val = categoryAxisId },
            new C.Scaling(new C.Orientation { Val = horizontal ? C.OrientationValues.MaxMin : C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = horizontal ? C.AxisPositionValues.Left : C.AxisPositionValues.Bottom },
            new C.NumberingFormat { FormatCode = "General", SourceLinked = true },
            new C.MajorTickMark { Val = C.TickMarkValues.None },
            new C.MinorTickMark { Val = C.TickMarkValues.None },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            RsdChartText(900),
            new C.CrossingAxis { Val = valueAxisId },
            new C.Crosses { Val = C.CrossesValues.AutoZero },
            new C.AutoLabeled { Val = true },
            new C.LabelAlignment { Val = C.LabelAlignmentValues.Center },
            new C.LabelOffset { Val = 100 },
            new C.NoMultiLevelLabels { Val = false }));

        plotArea.Append(new C.ValueAxis(
            new C.AxisId { Val = valueAxisId },
            new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
            new C.Delete { Val = false },
            new C.AxisPosition { Val = horizontal ? C.AxisPositionValues.Bottom : C.AxisPositionValues.Left },
            new C.MajorGridlines(new C.ChartShapeProperties(
                new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "D9D9D9" })))),
            new C.NumberingFormat { FormatCode = axisFormat, SourceLinked = false },
            new C.MajorTickMark { Val = C.TickMarkValues.None },
            new C.MinorTickMark { Val = C.TickMarkValues.None },
            new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
            RsdChartText(900),
            new C.CrossingAxis { Val = categoryAxisId },
            // With the categories read top-down, the value axis has to cross at their far end to stay at the bottom.
            new C.Crosses { Val = horizontal ? C.CrossesValues.Maximum : C.CrossesValues.AutoZero },
            new C.CrossBetween { Val = C.CrossBetweenValues.Between }));

        if (legend)
        {
            chart.Append(RsdLegend(C.LegendPositionValues.Bottom));
        }

        chart.Append(new C.PlotVisibleOnly { Val = true });
        chart.Append(new C.DisplayBlanksAs { Val = C.DisplayBlanksAsValues.Gap });

        chartPart.ChartSpace = chartSpace;
        chartPart.ChartSpace.Save();
    }

    /// <summary>A doughnut over a one-series block, a colour per category, labelled with its share.</summary>
    private static void BuildRsdDoughnutChart(ChartPart chartPart, string title, RsdBlock block, IReadOnlyList<string> colors)
    {
        var chartSpace = new C.ChartSpace();
        chartSpace.Append(new C.EditingLanguage { Val = "en-US" });
        chartSpace.Append(new C.RoundedCorners { Val = false });

        var chart = chartSpace.AppendChild(new C.Chart());
        chart.Append(RsdChartTitle(title));
        chart.Append(new C.AutoTitleDeleted { Val = false });

        var plotArea = chart.AppendChild(new C.PlotArea());
        plotArea.Append(new C.Layout());

        var doughnut = plotArea.AppendChild(new C.DoughnutChart());
        doughnut.Append(new C.VaryColors { Val = true });

        var series = new C.PieChartSeries(
            new C.Index { Val = 0 },
            new C.Order { Val = 0 },
            new C.SeriesText(new C.StringReference(new C.Formula(BuildSheetCellFormula(RsdChartsSheet, block.HeaderRow, RsdDataColumn + 1)))));

        for (var point = 0; point <= block.LastRow - block.FirstRow; point++)
        {
            series.Append(new C.DataPoint(
                new C.Index { Val = (uint)point },
                new C.Bubble3D { Val = false },
                new C.ChartShapeProperties(
                    new A.SolidFill(new A.RgbColorModelHex { Val = colors[point % colors.Count] }),
                    new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "FFFFFF" })))));
        }

        series.Append(new C.DataLabels(
            new C.NumberingFormat { FormatCode = "0.0%", SourceLinked = false },
            RsdChartText(900),
            new C.ShowLegendKey { Val = false },
            new C.ShowValue { Val = false },
            new C.ShowCategoryName { Val = false },
            new C.ShowSeriesName { Val = false },
            new C.ShowPercent { Val = true },
            new C.ShowBubbleSize { Val = false },
            new C.ShowLeaderLines { Val = true }));
        series.Append(new C.CategoryAxisData(new C.StringReference(new C.Formula(
            BuildSheetRangeFormula(RsdChartsSheet, block.FirstRow, RsdDataColumn, block.LastRow, RsdDataColumn)))));
        series.Append(new C.Values(new C.NumberReference(new C.Formula(
            BuildSheetRangeFormula(RsdChartsSheet, block.FirstRow, RsdDataColumn + 1, block.LastRow, RsdDataColumn + 1)))));
        doughnut.Append(series);

        doughnut.Append(new C.FirstSliceAngle { Val = 0 });
        doughnut.Append(new C.HoleSize { Val = 55 });

        chart.Append(RsdLegend(C.LegendPositionValues.Right));
        chart.Append(new C.PlotVisibleOnly { Val = true });
        chart.Append(new C.DisplayBlanksAs { Val = C.DisplayBlanksAsValues.Gap });

        chartPart.ChartSpace = chartSpace;
        chartPart.ChartSpace.Save();
    }

    private static C.Title RsdChartTitle(string text) => new(
        new C.ChartText(new C.RichText(
            new A.BodyProperties(),
            new A.ListStyle(),
            new A.Paragraph(
                new A.ParagraphProperties(new A.DefaultRunProperties { FontSize = 1200, Bold = true }),
                new A.Run(new A.RunProperties { Language = "en-US", FontSize = 1200, Bold = true }, new A.Text(text))))),
        new C.Overlay { Val = false });

    private static C.TextProperties RsdChartText(int size) => new(
        new A.BodyProperties(),
        new A.ListStyle(),
        new A.Paragraph(new A.ParagraphProperties(new A.DefaultRunProperties { FontSize = size })));

    private static C.Legend RsdLegend(C.LegendPositionValues position) => new(
        new C.LegendPosition { Val = position },
        new C.Layout(),
        new C.Overlay { Val = false },
        RsdChartText(900));

    // ── Routes ─────────────────────────────────────────────────────────────

    private static void WriteRsdRoutesSheet(
        IXLWorksheet ws,
        RouteCustomerSalesSummaryModel summary,
        IReadOnlyDictionary<string, string> routeLabels,
        IReadOnlyList<string> currencies,
        string? primary,
        bool salesGp)
    {
        var columns = RsdRouteColumns(routeLabels, currencies, primary, salesGp, RsdCosted(summary.Routes.SelectMany(route => route.TotalsByCurrency)));
        var row = WriteReportHeader(ws, "Routes", columns.Count, summary.From, summary.To);
        RsdNote(ws, row, columns.Count,
            $"One line per route. Trading bought in the period; dormant bought before but not for over {summary.DormantDays} days; " +
            "never bought has no purchase on any date. Totals follow the filter.");

        var table = WriteVpTable(ws, row + 2, columns, RouteSalesOverview.Routes(summary, primary).ToList(),
            "No route customers matched this report's filters.", totals: true, stripeByRule: true);
        WriteFooter(ws, table.NextRow - 1, columns.Count);
        FinalizeSheet(ws, columns.Count, table.HeaderRow, landscape: true, fitRows: RouteSheetFitRows);
    }

    private static List<VpColumn<RouteSalesRouteLine>> RsdRouteColumns(
        IReadOnlyDictionary<string, string> routeLabels,
        IReadOnlyList<string> currencies,
        string? primary,
        bool salesGp,
        IReadOnlySet<string> costed)
    {
        var columns = new List<VpColumn<RouteSalesRouteLine>>
        {
            new("Route", (cell, line) => cell.Value = RsdRouteLabel(routeLabels, line.Route.AssignedBusinessPartnerCode)),
            new("Customers", (cell, line) => VpNumber(cell, line.Route.CustomerCount, FormatCount), FormatCount),
            new("Trading", (cell, line) => VpNumber(cell, line.TradingCount, FormatCount), FormatCount),
            new("Dormant", (cell, line) => RsdCount(cell, line.Route.DormantCount, WarningOrange), FormatCount),
            new("Never bought", (cell, line) => RsdCount(cell, line.Route.NeverBoughtCount, DangerRed), FormatCount),
            new("Sales", (cell, line) => VpNumber(cell, line.Route.SaleCount, FormatCount), FormatCount)
        };

        foreach (var currency in currencies)
        {
            var code = currency;
            var format = MoneyFormatFor(code);
            columns.Add(new($"Value ({code})",
                (cell, line) => RsdOptionalNumber(cell, RouteSalesOverview.In(line.Route.TotalsByCurrency, code)?.Gross, format),
                format,
                Bar: code == primary));

            if (salesGp)
            {
                columns.Add(new($"Gross profit ({code})",
                    (cell, line) => VpOptionalMoney(cell, RouteSalesOverview.In(line.Route.TotalsByCurrency, code)?.GrossProfit, code),
                    costed.Contains(code) ? format : null));
                columns.Add(new($"Margin ({code})",
                    (cell, line) => RsdMargin(cell, RouteSalesOverview.In(line.Route.TotalsByCurrency, code)?.MarginPercent)));
            }
        }

        return columns;
    }

    // ── Customer products ──────────────────────────────────────────────────

    private sealed record RsdItemLine(RouteCustomerProductSalesRowModel Line, RouteCustomerSalesRowModel? Customer);

    private static void WriteRsdCustomerProductsSheet(
        IXLWorksheet ws,
        RouteCustomerSalesSummaryModel summary,
        RouteCustomerProductSalesModel? productSales,
        IReadOnlyDictionary<string, string> routeLabels,
        IReadOnlyList<string> currencies)
    {
        var rows = productSales?.Rows ?? [];
        var gp = productSales?.Margin is { Available: true };

        // The summary's currencies first, in its order; any the lines carry beyond them after.
        var lineCurrencies = currencies
            .Concat(rows.Select(line => line.Currency.Trim().ToUpperInvariant()))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var costed = rows
            .Where(line => line.GrossProfit is not null)
            .Select(line => line.Currency.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var customers = summary.Routes.SelectMany(route => route.Customers).ToList();
        var byId = customers
            .Where(customer => customer.RouteCustomerId is not null)
            .GroupBy(customer => customer.RouteCustomerId!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        var byCode = customers
            .GroupBy(customer => $"{customer.AssignedBusinessPartnerCode}|{customer.Code}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var lines = rows
            .Select(line => new RsdItemLine(line,
                line.RouteCustomerId is { } id && byId.TryGetValue(id, out var match)
                    ? match
                    : byCode.GetValueOrDefault($"{line.AssignedBusinessPartnerCode}|{line.RouteCustomerCode}")))
            .OrderBy(item => item.Line.AssignedBusinessPartnerCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Customer?.Name ?? item.Line.RouteCustomerCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Line.Currency, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.Line.LineTotal)
            .ToList();

        var columns = new List<VpColumn<RsdItemLine>>
        {
            new("Route", (cell, item) => cell.Value = RsdRouteLabel(routeLabels, item.Line.AssignedBusinessPartnerCode)),
            new("Customer code", (cell, item) => VpCode(cell, item.Line.RouteCustomerCode)),
            new("Customer", (cell, item) => cell.Value = item.Customer?.Name ?? "(customer deleted)"),
            new("Standing", (cell, item) => RsdStanding(cell, item.Customer, summary.DormantDays)),
            new("Item code", (cell, item) => VpCode(cell, item.Line.ItemCode)),
            new("Item", (cell, item) => cell.Value = RsdItemName(item.Line.ItemDescription, item.Line.ItemCode)),
            new("UoM", (cell, item) => cell.Value = item.Line.UoMCode ?? string.Empty),
            new("Quantity", (cell, item) => VpNumber(cell, item.Line.Quantity, FormatQuantity)),
            new("Times bought", (cell, item) => VpNumber(cell, item.Line.LineCount, FormatCount), FormatCount)
        };

        foreach (var currency in lineCurrencies)
        {
            var code = currency;
            var format = MoneyFormatFor(code);
            columns.Add(new($"Line value ({code})",
                (cell, item) => RsdOptionalNumber(cell, SameCurrency(item.Line.Currency, code) ? item.Line.LineTotal : null, format),
                format));

            if (gp)
            {
                columns.Add(new($"Gross profit ({code})",
                    (cell, item) => VpOptionalMoney(cell, SameCurrency(item.Line.Currency, code) ? item.Line.GrossProfit : null, code),
                    costed.Contains(code) ? format : null));
            }
        }

        if (gp)
        {
            columns.Add(new("Margin", (cell, item) => RsdMargin(cell, item.Line.MarginPercent)));
        }

        columns.Add(new("Last sold", (cell, item) => VpDate(cell, item.Line.LastSoldAt)));

        var row = WriteReportHeader(ws, "Customer products", columns.Count, summary.From, summary.To);
        RsdNote(ws, row, columns.Count,
            "Every item each route customer bought in the period: one row per customer, item and currency, at the value the " +
            "sale lines carry. Filter a route or a customer; the totals follow the filter.");

        var table = WriteVpTable(ws, row + 2, columns, lines, "No items were sold to route customers in this period.", totals: true, stripeByRule: true);
        WriteFooter(ws, table.NextRow - 1, columns.Count);
        FinalizeSheet(ws, columns.Count, table.HeaderRow, landscape: true, fitRows: RouteSheetFitRows);

        static bool SameCurrency(string? left, string right) =>
            string.Equals(left?.Trim(), right, StringComparison.OrdinalIgnoreCase);
    }

    // ── Products ───────────────────────────────────────────────────────────

    private static void WriteRsdProductsSheet(
        IXLWorksheet ws,
        RouteCustomerSalesSummaryModel summary,
        RouteCustomerProductMixModel? productMix,
        IReadOnlyList<string> currencies,
        bool productGp)
    {
        var columns = RsdProductColumns(currencies, productGp, withLastSold: true, RsdCosted((productMix?.Items ?? []).SelectMany(item => item.TotalsByCurrency)));
        var ranked = RouteSalesOverview.Products(productMix)
            .Select((item, index) => new RouteSalesRankedProduct(index + 1, item, 0m))
            .ToList();

        var row = WriteReportHeader(ws, "Products", columns.Count, summary.From, summary.To);
        RsdNote(ws, row, columns.Count,
            "Ordered by how often an item was bought rather than by value or quantity: value would add one currency to " +
            "another, and quantity would put a case above a bottle.");

        var table = WriteVpTable(ws, row + 2, columns, ranked, "Nothing was sold to route customers in this period.", totals: true, stripeByRule: true);
        WriteFooter(ws, table.NextRow - 1, columns.Count);
        FinalizeSheet(ws, columns.Count, table.HeaderRow, landscape: true, fitRows: RouteSheetFitRows);
    }

    private static List<VpColumn<RouteSalesRankedProduct>> RsdProductColumns(
        IReadOnlyList<string> currencies,
        bool productGp,
        bool withLastSold,
        IReadOnlySet<string> costed)
    {
        var columns = new List<VpColumn<RouteSalesRankedProduct>>
        {
            new("#", (cell, entry) => VpNumber(cell, entry.Rank, FormatCount)),
            new("Item code", (cell, entry) => VpCode(cell, entry.Item.ItemCode)),
            new("Item", (cell, entry) => cell.Value = RsdItemName(entry.Item.ItemDescription, entry.Item.ItemCode)),
            new("UoM", (cell, entry) => cell.Value = entry.Item.UoMCode ?? string.Empty),
            new("Quantity", (cell, entry) => VpNumber(cell, entry.Item.Quantity, FormatQuantity)),
            new("Times bought", (cell, entry) => VpNumber(cell, entry.Item.LineCount, FormatCount), FormatCount, Bar: true),
            new("Customers", (cell, entry) => VpNumber(cell, entry.Item.CustomerCount, FormatCount))
        };

        foreach (var currency in currencies)
        {
            var code = currency;
            var format = MoneyFormatFor(code);
            columns.Add(new($"Value ({code})",
                (cell, entry) => RsdOptionalNumber(cell, RouteSalesOverview.In(entry.Item.TotalsByCurrency, code)?.Gross, format),
                format));

            if (productGp)
            {
                columns.Add(new($"Gross profit ({code})",
                    (cell, entry) => VpOptionalMoney(cell, RouteSalesOverview.In(entry.Item.TotalsByCurrency, code)?.GrossProfit, code),
                    costed.Contains(code) ? format : null));
            }
        }

        if (withLastSold)
        {
            columns.Add(new("Last sold", (cell, entry) => VpDate(cell, entry.Item.LastSoldAt)));
        }

        return columns;
    }

    // ── Dormant ────────────────────────────────────────────────────────────

    private static void WriteRsdDormantSheet(
        IXLWorksheet ws,
        RouteCustomerSalesSummaryModel summary,
        IReadOnlyDictionary<string, string> routeLabels)
    {
        var columns = new List<VpColumn<RouteCustomerSalesRowModel>>
        {
            new("Customer", (cell, customer) => cell.Value = customer.Name),
            new("Code", (cell, customer) => VpCode(cell, customer.Code)),
            new("Route", (cell, customer) => cell.Value = RsdRouteLabel(routeLabels, customer.AssignedBusinessPartnerCode)),
            new("Phone", (cell, customer) => cell.Value = customer.Phone ?? string.Empty),
            new("Standing", (cell, customer) => RsdStanding(cell, customer, summary.DormantDays)),
            new("Last sale (all time)", (cell, customer) => VpDate(cell, customer.LastSaleAt)),
            new("Days since", (cell, customer) => RsdOptionalNumber(cell, customer.DaysSinceLastSale, FormatCount)),
            new("Captured", (cell, customer) => VpDate(cell, customer.CapturedAt))
        };

        var row = WriteReportHeader(ws, "Needs a visit", columns.Count, summary.From, summary.To);
        RsdNote(ws, row, columns.Count,
            $"Dormant customers (bought before, nothing for over {summary.DormantDays} days) longest silence first, then the " +
            "customers who have never bought. Both are read over all time, not over the dates above.");

        var table = WriteVpTable(ws, row + 2, columns, RouteSalesOverview.NeedsAVisit(summary).ToList(),
            "Every customer on this filter is trading.", totals: false, stripeByRule: true);
        WriteFooter(ws, table.NextRow - 1, columns.Count);
        FinalizeSheet(ws, columns.Count, table.HeaderRow, landscape: true, fitRows: RouteSheetFitRows);
    }

    // ── Cells ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The currencies SAP has costed anything in. A gross profit column in any other gets no total: a SUBTOTAL
    /// over blanks is 0.00, which would read as break-even rather than "not in SAP yet".
    /// </summary>
    private static HashSet<string> RsdCosted(IEnumerable<RouteCustomerSalesTotalsModel> totals) =>
        totals
            .Where(total => total.GrossProfit is not null && !string.IsNullOrWhiteSpace(total.Currency))
            .Select(total => total.Currency.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string RsdRouteLabel(IReadOnlyDictionary<string, string> routeLabels, string code) =>
        routeLabels.TryGetValue(code, out var label) ? label : code;

    private static string RsdItemName(string? description, string code) =>
        string.IsNullOrWhiteSpace(description) ? code : description.Trim();

    /// <summary>The API states a margin as a percentage (42.5); the cell holds the fraction so it formats as one.</summary>
    private static void RsdMargin(IXLCell cell, decimal? marginPercent)
    {
        if (marginPercent is { } margin)
        {
            VpNumber(cell, margin / 100m, FormatPercent);
        }
    }

    /// <summary>Blank rather than a dash or zero when there is no figure, so the column still sorts as numbers.</summary>
    private static void RsdOptionalNumber(IXLCell cell, decimal? value, string format)
    {
        if (value is { } number)
        {
            VpNumber(cell, number, format);
        }
    }

    private static void RsdCount(IXLCell cell, int value, XLColor color)
    {
        VpNumber(cell, value, FormatCount);
        if (value > 0)
        {
            cell.Style.Font.FontColor = color;
        }
    }

    private static void RsdStanding(IXLCell cell, RouteCustomerSalesRowModel? customer, int dormantDays)
    {
        if (customer is null)
        {
            return;
        }

        var standing = RouteSalesOverview.Standing(customer, dormantDays);
        cell.Value = standing;
        cell.Style.Font.FontColor = standing switch
        {
            "Never bought" => DangerRed,
            "Dormant" => WarningOrange,
            _ => SuccessGreen
        };
    }

    /// <summary>A line of explanation under the letterhead, across the sheet.</summary>
    private static void RsdNote(IXLWorksheet ws, int row, int lastCol, string text)
    {
        var range = ws.Range(row, 1, row, lastCol);
        range.Merge();
        range.Style.Alignment.WrapText = true;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        ws.Cell(row, 1).Value = text;
        ws.Cell(row, 1).Style.Font.FontSize = 9;
        ws.Cell(row, 1).Style.Font.FontColor = MutedText;
        ws.Row(row).Height = 30;
    }
}
