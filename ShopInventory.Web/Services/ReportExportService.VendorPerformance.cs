using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using ShopInventory.Web.Features.Vending;

namespace ShopInventory.Web.Services;

/// <summary>
/// The Depots &amp; vendors workbook: a summary, a sheet per depot, every vendor, every item, and every
/// vendor's items. The figures are worked out by <see cref="VendorPerformanceBuilder"/>; this lays them out.
/// </summary>
/// <remarks>
/// The tables are written from column lists rather than cell by cell, because the money columns are one per
/// currency traded: a month in USD alone has one sales column, a month in USD and ZWG has two, and every
/// table, total and chart has to find its columns either way.
/// </remarks>
public partial class ReportExportService
{
    /// <summary>One column of a table: its heading, how a row fills it, and how — if at all — it totals.</summary>
    private sealed record VpColumn<T>(
        string Header,
        Action<IXLCell, T> Write,
        string? TotalFormat = null,
        bool Bar = false);

    private sealed record VpTable(int HeaderRow, int FirstRow, int LastRow, int NextRow);

    private const string FormatChange = "[Color10]+0.0%;[Red]-0.0%;0.0%";
    private static readonly XLColor VpBarColor = XLColor.FromHtml("#93C5FD");

    public byte[] ExportVendorPerformanceToExcel(VendorPerformanceReport report)
    {
        const string title = "Vending vendor performance";
        using var workbook = NewWorkbook(title);

        var summary = WriteVpSummarySheet(workbook, report, title);

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Summary", "All vendors", "Products", "Vendor products" };
        foreach (var depot in report.Depots)
        {
            WriteVpDepotSheet(workbook, report, depot, VpSheetName(depot.Name, usedNames));
        }

        WriteVpAllVendorsSheet(workbook, report);
        var products = WriteVpProductsSheet(workbook, report);
        WriteVpVendorProductsSheet(workbook, report);

        return AddVpCharts(WorkbookToBytes(workbook), report, summary, products);
    }

    // ── Sheets ─────────────────────────────────────────────────────────────

    private sealed record VpSummaryLayout(VpTable Depots, int SalesColumn, int PreviousColumn, int ChartTopRow, int ChartBottomRow, int LastCol);

    private sealed record VpProductsLayout(VpTable Table, int DescriptionColumn, int ValueColumn);

    private VpSummaryLayout WriteVpSummarySheet(XLWorkbook workbook, VendorPerformanceReport report, string title)
    {
        var ws = AddSheet(workbook, "Summary");
        var currency = report.Currency;
        var columns = VpDepotColumns(report);
        var lastCol = columns.Count;

        var row = WriteReportHeader(ws, $"{title} — {report.ScopeLabel}", lastCol, report.From, report.To);
        row = WriteVpKpis(ws, row, currency, report.Totals.Gross[currency], report.Totals.Net[currency],
            report.Totals.Change, report.Totals.Gross[currency] > 0 && report.Totals.PreviousGross[currency] <= 0,
            report.Totals.Invoices, report.Totals.SellingVendors, report.Totals.ActiveVendors, report.Totals.AverageInvoice);

        row = WriteNotice(ws, row, lastCol,
            $"Compared with {report.PreviousFrom:dd MMM yyyy} – {report.PreviousTo:dd MMM yyyy}. " +
            $"{report.Totals.ItemsSold:N0} items sold by {report.Totals.SellingVendors:N0} vendors on {report.Totals.Invoices:N0} invoices.",
            LightNavy);
        if (report.Currencies.Count > 1)
        {
            row = WriteNotice(ws, row, lastCol, VpCurrencyNotice(report), WarningOrange);
        }

        row++;
        WriteSectionTitle(ws, row++, lastCol, "Sales by depot");
        var depots = WriteVpTable(ws, row, columns, report.Depots, "No depots are set up.", totals: true);
        row = depots.NextRow + 1;

        // Space for the two charts, which are drawn into the saved file afterwards — ClosedXML cannot
        // write a chart.
        WriteSectionTitle(ws, row++, lastCol, $"Sales by depot against the previous period, and the ten best-selling items ({currency})");
        var chartTop = row;
        row += 18;

        WriteSectionTitle(ws, row++, lastCol, "Top ten vendors");
        var top = WriteVpTable(ws, row, VpTopVendorColumns(report), report.Vendors.Take(10).ToList(),
            "No vendor sold anything in these dates.", totals: false, filter: false);
        row = top.NextRow + 1;

        WriteSectionTitle(ws, row++, lastCol, "Reading this workbook");
        foreach (var note in VpReadingNotes(report))
        {
            row = WriteNotice(ws, row, lastCol, note, LightNavy);
        }

        WriteFooter(ws, row - 1, lastCol);
        FinalizeSheet(ws, lastCol, landscape: true, fitFromRow: depots.HeaderRow);

        return new VpSummaryLayout(
            depots,
            SalesColumn: VpColumnIndex(columns, VpSalesHeader(currency)),
            PreviousColumn: VpColumnIndex(columns, VpPreviousHeader(currency)),
            ChartTopRow: chartTop,
            ChartBottomRow: chartTop + 17,
            LastCol: lastCol);
    }

    private void WriteVpDepotSheet(XLWorkbook workbook, VendorPerformanceReport report, DepotPerformance depot, string sheetName)
    {
        var ws = AddSheet(workbook, sheetName, LightNavy);
        var currency = report.Currency;
        var vendorColumns = VpVendorColumns(report, includeDepot: false);
        var productColumns = VpProductColumns(report, depotColumns: false);
        var lastCol = Math.Max(vendorColumns.Count, productColumns.Count);

        // The cards sit over the money columns: the first few here are Rank and Code, too narrow for a total.
        var cardColumn = VpColumnIndex(vendorColumns, VpSalesHeader(currency));
        var row = WriteReportHeader(ws, depot.Name, lastCol, report.From, report.To);
        row = WriteVpKpis(ws, row, currency, depot.Gross[currency], depot.Net[currency],
            depot.Change, depot.Gross[currency] > 0 && depot.PreviousGross[currency] <= 0,
            depot.Invoices, depot.SellingVendors, depot.ActiveVendors, depot.AverageInvoice,
            cardColumn > 0 && cardColumn + 5 <= lastCol ? cardColumn : 1);

        var warehouses = depot.WarehouseCodes.Count == 0 ? "no warehouse" : string.Join(", ", depot.WarehouseCodes);
        row = WriteNotice(ws, row, lastCol,
            $"{depot.Code} · warehouse {warehouses} · {depot.Cashiers:N0} cashier(s). " +
            $"Compared with {report.PreviousFrom:dd MMM yyyy} – {report.PreviousTo:dd MMM yyyy}, " +
            $"when it took {VpMoneyText(depot.PreviousGross[currency], currency)} on {depot.PreviousInvoices:N0} invoices.",
            LightNavy);

        row++;
        WriteSectionTitle(ws, row++, lastCol, "Vendors, best first");
        var vendors = WriteVpTable(ws, row, vendorColumns, depot.Vendors,
            "This depot has no vendors.", totals: true);
        row = vendors.NextRow + 1;

        WriteSectionTitle(ws, row++, lastCol, "Items sold at this depot, best first");
        var products = WriteVpTable(ws, row, productColumns, depot.Products,
            "Nothing was sold at this depot in these dates.", totals: true, filter: false);
        VpColourClasses(ws, products, productColumns);
        row = products.NextRow;

        WriteFooter(ws, row - 1, lastCol);
        FinalizeSheet(ws, lastCol, landscape: true, fitFromRow: vendors.HeaderRow);
    }

    private void WriteVpAllVendorsSheet(XLWorkbook workbook, VendorPerformanceReport report)
    {
        var ws = AddSheet(workbook, "All vendors");
        var columns = VpVendorColumns(report, includeDepot: true);
        var lastCol = columns.Count;

        var row = WriteReportHeader(ws, $"Every vendor — {report.ScopeLabel}", lastCol, report.From, report.To);
        var table = WriteVpTable(ws, row, columns, report.Vendors, "No vendors matched.", totals: true);

        WriteFooter(ws, table.NextRow - 1, lastCol);
        FinalizeSheet(ws, lastCol, table.HeaderRow, landscape: true);
    }

    private VpProductsLayout WriteVpProductsSheet(XLWorkbook workbook, VendorPerformanceReport report)
    {
        var ws = AddSheet(workbook, "Products");
        var columns = VpProductColumns(report, depotColumns: report.Depots.Count > 1);
        var lastCol = columns.Count;

        var row = WriteReportHeader(ws, $"Product performance — {report.ScopeLabel}", lastCol, report.From, report.To);
        row = WriteNotice(ws, row, lastCol,
            $"Values are before VAT, in {report.Currency} unless a column says otherwise. " +
            "Class A items make the first 80% of sales, B the next 15%, C the rest. " +
            $"Reach is the share of the {report.Totals.SellingVendors:N0} vendors who sold anything that sold this item. " +
            $"Change is against {report.PreviousFrom:dd MMM yyyy} – {report.PreviousTo:dd MMM yyyy}.",
            LightNavy);

        var table = WriteVpTable(ws, row, columns, report.Products, "Nothing was sold in these dates.", totals: true);
        VpColourClasses(ws, table, columns);

        WriteFooter(ws, table.NextRow - 1, lastCol);
        FinalizeSheet(ws, lastCol, table.HeaderRow, landscape: true);

        return new VpProductsLayout(table, VpColumnIndex(columns, "Description"), VpColumnIndex(columns, VpNetHeader(report.Currency)));
    }

    private void WriteVpVendorProductsSheet(XLWorkbook workbook, VendorPerformanceReport report)
    {
        var ws = AddSheet(workbook, "Vendor products");
        var single = report.Currencies.Count == 1;

        var columns = new List<VpColumn<VendorProductPerformance>>
        {
            new("Depot", (cell, item) => cell.Value = item.DepotName),
            new("Vendor code", (cell, item) => VpCode(cell, item.VendorCode)),
            new("Vendor", (cell, item) => cell.Value = item.VendorName),
            new("Item", (cell, item) => VpCode(cell, item.ItemCode)),
            new("Description", (cell, item) => cell.Value = item.Description ?? string.Empty),
            new("UoM", (cell, item) => VpCentred(cell, item.UoMCode ?? "-")),
        };
        if (!single)
        {
            columns.Add(new("Currency", (cell, item) => VpCentred(cell, item.Currency)));
        }

        columns.AddRange(
        [
            new("Quantity", (cell, item) => VpNumber(cell, item.Quantity, FormatQuantity)),
            new(single ? VpNetHeader(report.Currency) : "Net value (excl. VAT)",
                (cell, item) => VpNumber(cell, item.Net, MoneyFormatFor(item.Currency)),
                single ? MoneyFormatFor(report.Currency) : null),
            new("Share of vendor", (cell, item) => VpPercent(cell, item.ShareOfVendor), Bar: true),
            new("Lines", (cell, item) => VpNumber(cell, item.Lines, FormatCount), FormatCount),
            new("Last sold", (cell, item) => VpDate(cell, item.LastSoldAt)),
        ]);

        var lastCol = columns.Count;
        var row = WriteReportHeader(ws, $"Items by vendor — {report.ScopeLabel}", lastCol, report.From, report.To);
        var table = WriteVpTable(ws, row, columns, report.VendorProducts, "Nothing was sold in these dates.", totals: true);

        WriteFooter(ws, table.NextRow - 1, lastCol);
        FinalizeSheet(ws, lastCol, table.HeaderRow, landscape: true);
    }

    // ── Column lists ───────────────────────────────────────────────────────

    private static List<VpColumn<DepotPerformance>> VpDepotColumns(VendorPerformanceReport report)
    {
        var currency = report.Currency;
        var columns = new List<VpColumn<DepotPerformance>>
        {
            new("Depot", (cell, depot) => { cell.Value = depot.Name; cell.Style.Font.Bold = true; }),
            new("Code", (cell, depot) => VpCode(cell, depot.Code)),
            new("Cashiers", (cell, depot) => VpNumber(cell, depot.Cashiers, FormatCount), FormatCount),
            new("Vendors", (cell, depot) => VpNumber(cell, depot.ActiveVendors, FormatCount), FormatCount),
            new("Vendors selling", (cell, depot) => VpNumber(cell, depot.SellingVendors, FormatCount), FormatCount),
            new("Selling %", (cell, depot) => VpPercent(cell, depot.ActiveVendors > 0 ? (decimal)depot.SellingVendors / depot.ActiveVendors : null)),
            new("Invoices", (cell, depot) => VpNumber(cell, depot.Invoices, FormatCount), FormatCount),
        };

        foreach (var code in report.Currencies)
        {
            columns.Add(new(VpSalesHeader(code), (cell, depot) => VpNumber(cell, depot.Gross[code], MoneyFormatFor(code)), MoneyFormatFor(code)));
        }

        foreach (var code in report.Currencies)
        {
            columns.Add(new(VpNetHeader(code), (cell, depot) => VpNumber(cell, depot.Net[code], MoneyFormatFor(code)), MoneyFormatFor(code)));
        }

        columns.AddRange(
        [
            new($"Gross profit ({currency})", (cell, depot) => VpGrossProfit(cell, depot.GrossProfit, currency), MoneyFormatFor(currency)),
            new("Margin", (cell, depot) => VpPercent(cell, CurrencyAmounts.Margin(depot.GrossProfit, depot.CostedNet, currency))),
            new("Share of sales", (cell, depot) => VpPercent(cell, depot.Share), Bar: true),
            new(VpPreviousHeader(currency), (cell, depot) => VpNumber(cell, depot.PreviousGross[currency], MoneyFormatFor(currency)), MoneyFormatFor(currency)),
            new("Change", (cell, depot) => VpChange(cell, depot.Change, depot.Gross[currency], depot.PreviousGross[currency])),
            new($"Per selling vendor ({currency})", (cell, depot) => VpOptionalMoney(cell, depot.AveragePerSellingVendor, currency)),
            new($"Average invoice ({currency})", (cell, depot) => VpOptionalMoney(cell, depot.AverageInvoice, currency)),
            new("Items sold", (cell, depot) => VpNumber(cell, depot.Products.Count, FormatCount)),
            new("Best seller", (cell, depot) => cell.Value = depot.Products.FirstOrDefault() is { } best ? VpItemLabel(best.ItemCode, best.Description) : "—"),
        ]);

        return columns;
    }

    private static List<VpColumn<VendorPerformance>> VpVendorColumns(VendorPerformanceReport report, bool includeDepot)
    {
        var currency = report.Currency;
        var columns = new List<VpColumn<VendorPerformance>>
        {
            new("Rank", (cell, vendor) => VpNumber(cell, includeDepot ? vendor.Rank : vendor.RankInDepot, FormatCount)),
        };

        if (includeDepot)
        {
            columns.Add(new("Depot", (cell, vendor) => cell.Value = vendor.DepotName));
            columns.Add(new("Rank in depot", (cell, vendor) => VpNumber(cell, vendor.RankInDepot, FormatCount)));
        }

        columns.AddRange(
        [
            new("Code", (cell, vendor) => VpCode(cell, vendor.Code)),
            new("Vendor", (cell, vendor) => cell.Value = vendor.Name),
            new("Phone", (cell, vendor) => cell.Value = vendor.Phone ?? "-"),
            new("Status", (cell, vendor) => VpStatus(cell, vendor)),
            new("Standing", (cell, vendor) => VpStanding(cell, vendor)),
            new("Invoices", (cell, vendor) => VpNumber(cell, vendor.Invoices, FormatCount), FormatCount),
            new("Lines", (cell, vendor) => VpNumber(cell, vendor.Lines, FormatCount), FormatCount),
        ]);

        foreach (var code in report.Currencies)
        {
            columns.Add(new(VpSalesHeader(code), (cell, vendor) => VpNumber(cell, vendor.Gross[code], MoneyFormatFor(code)), MoneyFormatFor(code)));
        }

        foreach (var code in report.Currencies)
        {
            columns.Add(new(VpNetHeader(code), (cell, vendor) => VpNumber(cell, vendor.Net[code], MoneyFormatFor(code)), MoneyFormatFor(code)));
        }

        columns.Add(new($"Gross profit ({currency})", (cell, vendor) => VpGrossProfit(cell, vendor.GrossProfit, currency), MoneyFormatFor(currency)));
        columns.Add(new("Margin", (cell, vendor) => VpPercent(cell, CurrencyAmounts.Margin(vendor.GrossProfit, vendor.CostedNet, currency))));

        if (includeDepot)
        {
            columns.Add(new("Share of all sales", (cell, vendor) => VpPercent(cell, vendor.ShareOfAll), Bar: true));
        }

        columns.AddRange(
        [
            new("Share of depot", (cell, vendor) => VpPercent(cell, vendor.ShareOfDepot), Bar: !includeDepot),
            new($"Average invoice ({currency})", (cell, vendor) => VpOptionalMoney(cell, vendor.AverageInvoice, currency)),
            new(VpPreviousHeader(currency), (cell, vendor) => VpNumber(cell, vendor.PreviousGross[currency], MoneyFormatFor(currency)), MoneyFormatFor(currency)),
            new("Change", (cell, vendor) => VpChange(cell, vendor.Change, vendor.Gross[currency], vendor.PreviousGross[currency])),
            new("Items", (cell, vendor) => VpNumber(cell, vendor.ItemCount, FormatCount)),
            new("Best seller", (cell, vendor) => cell.Value = vendor.TopItemCode is null ? "—" : VpItemLabel(vendor.TopItemCode, vendor.TopItemDescription)),
            new("Last sale (all time)", (cell, vendor) => VpDate(cell, vendor.LastSaleAt)),
            new("Days since", (cell, vendor) =>
            {
                if (vendor.DaysSinceLastSale is { } days)
                {
                    VpNumber(cell, days, FormatCount);
                }
            }),
        ]);

        return columns;
    }

    private static List<VpColumn<VendorPerformance>> VpTopVendorColumns(VendorPerformanceReport report)
    {
        var currency = report.Currency;
        return
        [
            new("Rank", (cell, vendor) => VpNumber(cell, vendor.Rank, FormatCount)),
            new("Vendor", (cell, vendor) => { cell.Value = vendor.Name; cell.Style.Font.Bold = true; }),
            new("Code", (cell, vendor) => VpCode(cell, vendor.Code)),
            new("Depot", (cell, vendor) => cell.Value = vendor.DepotName),
            new("Invoices", (cell, vendor) => VpNumber(cell, vendor.Invoices, FormatCount)),
            new(VpSalesHeader(currency), (cell, vendor) => VpNumber(cell, vendor.Gross[currency], MoneyFormatFor(currency))),
            new("Share of all sales", (cell, vendor) => VpPercent(cell, vendor.ShareOfAll)),
            new("Change", (cell, vendor) => VpChange(cell, vendor.Change, vendor.Gross[currency], vendor.PreviousGross[currency])),
            new("Best seller", (cell, vendor) => cell.Value = vendor.TopItemCode is null ? "—" : VpItemLabel(vendor.TopItemCode, vendor.TopItemDescription)),
        ];
    }

    private static List<VpColumn<ProductPerformance>> VpProductColumns(VendorPerformanceReport report, bool depotColumns)
    {
        var currency = report.Currency;
        var columns = new List<VpColumn<ProductPerformance>>
        {
            new("Rank", (cell, item) => VpNumber(cell, item.Rank, FormatCount)),
            new("Item", (cell, item) => VpCode(cell, item.ItemCode)),
            // The item code stands in for a missing description, so the chart drawn from this column
            // never has an unlabelled bar.
            new("Description", (cell, item) => cell.Value = string.IsNullOrWhiteSpace(item.Description) ? item.ItemCode : item.Description),
            new("UoM", (cell, item) => VpCentred(cell, item.UoMCode ?? "-")),
            new("Quantity", (cell, item) => VpNumber(cell, item.Quantity, FormatQuantity)),
        };

        foreach (var code in report.Currencies)
        {
            columns.Add(new(VpNetHeader(code), (cell, item) => VpNumber(cell, item.Net[code], MoneyFormatFor(code)), MoneyFormatFor(code)));
        }

        columns.AddRange(
        [
            new($"Gross profit ({currency})", (cell, item) => VpGrossProfit(cell, item.GrossProfit, currency), MoneyFormatFor(currency)),
            new("Margin", (cell, item) => VpPercent(cell, CurrencyAmounts.Margin(item.GrossProfit, item.CostedNet, currency))),
            new("Share", (cell, item) => VpPercent(cell, item.Share), Bar: true),
            new("Cumulative", (cell, item) => VpPercent(cell, item.CumulativeShare)),
            new("Class", (cell, item) => VpCentred(cell, item.Class.ToString())),
            new("Lines", (cell, item) => VpNumber(cell, item.Lines, FormatCount), FormatCount),
            new("Vendors", (cell, item) => VpNumber(cell, item.Vendors, FormatCount)),
            new("Reach", (cell, item) => VpPercent(cell, item.Reach)),
            new($"Average price ({currency})", (cell, item) => VpOptionalMoney(cell, item.AveragePrice, currency)),
            new($"Previous period net ({currency})", (cell, item) => VpNumber(cell, item.PreviousNet[currency], MoneyFormatFor(currency)), MoneyFormatFor(currency)),
            new("Change", (cell, item) => VpChange(cell, item.Change, item.Net[currency], item.PreviousNet[currency])),
        ]);

        if (depotColumns)
        {
            foreach (var depot in report.Depots)
            {
                var code = depot.Code;
                columns.Add(new($"{depot.Name} ({currency})",
                    (cell, item) => VpNumber(cell, item.NetByDepot.GetValueOrDefault(code), MoneyFormatFor(currency)),
                    MoneyFormatFor(currency)));
            }
        }

        columns.Add(new("Last sold", (cell, item) => VpDate(cell, item.LastSoldAt)));
        return columns;
    }

    // ── Writing ────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes one table from its column list, with a SUBTOTAL row under the columns that total — so the
    /// total follows the filter — and a data bar down the columns that compare rows.
    /// </summary>
    private static VpTable WriteVpTable<T>(
        IXLWorksheet ws,
        int headerRow,
        IReadOnlyList<VpColumn<T>> columns,
        IReadOnlyCollection<T> rows,
        string emptyMessage,
        bool totals,
        bool filter = true,
        bool stripeByRule = false)
    {
        var lastCol = columns.Count;
        for (var index = 0; index < columns.Count; index++)
        {
            ws.Cell(headerRow, index + 1).Value = columns[index].Header;
        }

        StyleTableHeader(ws, headerRow, lastCol);

        var row = headerRow + 1;
        foreach (var item in rows)
        {
            for (var index = 0; index < columns.Count; index++)
            {
                columns[index].Write(ws.Cell(row, index + 1), item);
            }

            row++;
        }

        var firstRow = headerRow + 1;
        var lastRow = row - 1;
        var next = FinishTable(ws, headerRow, firstRow, row, lastCol, emptyMessage, filter, stripeByRule);

        if (lastRow < firstRow)
        {
            return new VpTable(headerRow, firstRow, lastRow, next);
        }

        for (var index = 0; index < columns.Count; index++)
        {
            if (columns[index].Bar && lastRow > firstRow)
            {
                ws.Range(firstRow, index + 1, lastRow, index + 1)
                    .AddConditionalFormat()
                    .DataBar(VpBarColor)
                    .LowestValue()
                    .HighestValue();
            }
        }

        if (totals && columns.Any(column => column.TotalFormat is not null))
        {
            ws.Cell(next, 1).Value = "Total";
            for (var index = 0; index < columns.Count; index++)
            {
                if (columns[index].TotalFormat is { } format)
                {
                    WriteSubtotal(ws, next, index + 1, firstRow, lastRow, format);
                }
            }

            StyleTotalsRow(ws, next, lastCol);
            next++;
        }

        return new VpTable(headerRow, firstRow, lastRow, next);
    }

    private static int WriteVpKpis(
        IXLWorksheet ws,
        int row,
        string currency,
        decimal gross,
        decimal net,
        decimal? change,
        bool isNew,
        int invoices,
        int selling,
        int vendors,
        decimal? averageInvoice,
        int firstColumn = 1)
    {
        var c = firstColumn;
        WriteKpiCard(ws, row, c, $"Sales {currency} incl. VAT", gross, MoneyFormatFor(currency));
        WriteKpiCard(ws, row, c + 1, $"Net {currency} excl. VAT", net, MoneyFormatFor(currency));

        if (change is { } value)
        {
            WriteKpiCard(ws, row, c + 2, "Against previous period", value, FormatChange, value < 0 ? DangerRed : SuccessGreen);
        }
        else
        {
            WriteKpiCard(ws, row, c + 2, "Against previous period", isNew ? "New" : "—", MutedText);
        }

        WriteKpiCard(ws, row, c + 3, "Invoices", invoices, FormatCount);
        WriteKpiCard(ws, row, c + 4, "Vendors selling", $"{selling:N0} of {vendors:N0}");

        if (averageInvoice is { } average)
        {
            WriteKpiCard(ws, row, c + 5, $"Average invoice ({currency})", average, MoneyFormatFor(currency));
        }
        else
        {
            WriteKpiCard(ws, row, c + 5, $"Average invoice ({currency})", "—", MutedText);
        }

        return row + 3;
    }

    /// <summary>A, B and C in their own colours. Font only: the row stripes are fills and would paint over one.</summary>
    private static void VpColourClasses<T>(IXLWorksheet ws, VpTable table, IReadOnlyList<VpColumn<T>> columns)
    {
        var column = VpColumnIndex(columns, "Class");
        if (column == 0)
        {
            return;
        }

        for (var row = table.FirstRow; row <= table.LastRow; row++)
        {
            var cell = ws.Cell(row, column);
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = cell.GetString() switch
            {
                "A" => SuccessGreen,
                "B" => WarningOrange,
                _ => MutedText
            };
        }
    }

    private static void VpNumber(IXLCell cell, decimal value, string format)
    {
        cell.Value = value;
        cell.Style.NumberFormat.Format = format;
    }

    /// <summary>
    /// SAP's booked gross profit in one currency, or blank where none of the sales in it is in SAP yet: a
    /// zero would say they broke even.
    /// </summary>
    private static void VpGrossProfit(IXLCell cell, CurrencyAmounts profit, string currency)
    {
        if (profit.Has(currency))
        {
            VpNumber(cell, profit[currency], MoneyFormatFor(currency));
        }
    }

    private static void VpOptionalMoney(IXLCell cell, decimal? value, string currency)
    {
        if (value is { } amount)
        {
            VpNumber(cell, amount, MoneyFormatFor(currency));
        }
    }

    /// <summary>Blank rather than a dash when there is no figure, so the column still sorts as numbers.</summary>
    private static void VpPercent(IXLCell cell, decimal? value)
    {
        if (value is { } share)
        {
            VpNumber(cell, share, FormatPercent);
        }
    }

    /// <summary>
    /// The change, green up and red down. A vendor who sold nothing in the previous period has no
    /// percentage to grow by, and says "New" instead of a blank that reads as no change.
    /// </summary>
    private static void VpChange(IXLCell cell, decimal? change, decimal current, decimal previous)
    {
        if (change is { } value)
        {
            VpNumber(cell, value, FormatChange);
        }
        else if (current > 0 && previous <= 0)
        {
            cell.Value = "New";
            cell.Style.Font.FontColor = SuccessGreen;
            cell.Style.Font.Italic = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        }
    }

    private static void VpDate(IXLCell cell, DateTime? value)
    {
        if (value is { } date)
        {
            cell.Value = date;
            cell.Style.NumberFormat.Format = FormatDate;
        }
    }

    private static void VpCode(IXLCell cell, string code)
    {
        cell.Value = code;
        cell.Style.Font.Bold = true;
    }

    private static void VpCentred(IXLCell cell, string value)
    {
        cell.Value = value;
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static void VpStatus(IXLCell cell, VendorPerformance vendor)
    {
        VpCentred(cell, vendor.IsActive ? "Active" : "Removed");
        cell.Style.Font.FontColor = vendor.IsActive ? SuccessGreen : MutedText;
    }

    private static void VpStanding(IXLCell cell, VendorPerformance vendor)
    {
        (string text, XLColor colour) = vendor.Standing switch
        {
            VendorStanding.Selling => ("Selling", SuccessGreen),
            VendorStanding.Quiet => ("No sales in period", WarningOrange),
            _ => ("Never sold", DangerRed)
        };

        VpCentred(cell, text);
        cell.Style.Font.FontColor = colour;
    }

    private static string VpItemLabel(string code, string? description) =>
        string.IsNullOrWhiteSpace(description) ? code : $"{code} · {description}";

    private static string VpSalesHeader(string currency) => $"Sales {currency} (incl. VAT)";

    private static string VpNetHeader(string currency) => $"Net {currency} (excl. VAT)";

    private static string VpPreviousHeader(string currency) => $"Previous period ({currency})";

    private static string VpMoneyText(decimal value, string currency) =>
        $"{currency} {value.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)}";

    private static int VpColumnIndex<T>(IReadOnlyList<VpColumn<T>> columns, string header)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (columns[index].Header == header)
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static string VpCurrencyNotice(VendorPerformanceReport report) =>
        $"Sales were taken in {string.Join(" and ", report.Currencies)}. Each currency has its own columns and is " +
        $"never added to another; shares, ranks, averages and changes are read in {report.Currency}.";

    private static IEnumerable<string> VpReadingNotes(VendorPerformanceReport report)
    {
        yield return "Sales are as invoiced, VAT included — the same figures as the Depots & vendors page. " +
            "Net is sales less the VAT each invoice carried.";
        yield return "Item values are the invoice lines before VAT, so a vendor's items add up to their Net column, " +
            "not to their sales.";
        yield return $"Change compares these dates with {report.PreviousFrom:dd MMM yyyy} – {report.PreviousTo:dd MMM yyyy}. " +
            "A month to date is set against the same days of the month before. \"New\" means nothing was sold then.";
        yield return "Standing: Selling sold in these dates; No sales in period has sold before but not now; " +
            "Never sold has never made a sale. Last sale and Days since are all time, not limited to these dates.";
        yield return "Each depot has its own sheet. All vendors ranks every vendor together; Products ranks every item " +
            "with its A/B/C class and each depot's share; Vendor products lists every item each vendor sold.";
    }

    /// <summary>Excel's rules: 31 characters, none of : \ / ? * [ ], and no two sheets alike.</summary>
    private static string VpSheetName(string name, HashSet<string> used)
    {
        var clean = new string(name.Select(ch => ":\\/?*[]".Contains(ch) ? ' ' : ch).ToArray()).Trim();
        if (clean.Length == 0)
        {
            clean = "Depot";
        }

        var candidate = clean.Length > 31 ? clean[..31] : clean;
        for (var suffix = 2; !used.Add(candidate); suffix++)
        {
            var tail = $" ({suffix})";
            candidate = (clean.Length > 31 - tail.Length ? clean[..(31 - tail.Length)] : clean) + tail;
        }

        return candidate;
    }

    // ── Charts ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Finishes the saved file with the OpenXML SDK: the two charts on the summary, and the data bars'
    /// rule ids put right.
    /// </summary>
    /// <remarks>
    /// ClosedXML writes an Excel 2010 data bar's id as a lower-case GUID, and the schema allows only upper
    /// case — a file Excel may open with a repair prompt, and repair by dropping the bars.
    /// </remarks>
    private static byte[] AddVpCharts(byte[] workbookBytes, VendorPerformanceReport report, VpSummaryLayout summary, VpProductsLayout products)
    {
        // A chart of zeros says nothing a blank space does not, so the depots get one only with sales to show.
        var anySales = report.Totals.Gross[report.Currency] > 0 || report.Totals.PreviousGross[report.Currency] > 0;
        var hasDepots = anySales && summary.Depots.LastRow >= summary.Depots.FirstRow && summary.SalesColumn > 0 && summary.PreviousColumn > 0;
        var hasProducts = products.Table.LastRow >= products.Table.FirstRow && products.ValueColumn > 0;

        using var stream = new MemoryStream();
        stream.Write(workbookBytes, 0, workbookBytes.Length);
        stream.Position = 0;

        // Markers are zero-based, so the first reserved row (one-based) is its own number less one.
        var fromRow = summary.ChartTopRow - 1;
        var toRow = summary.ChartBottomRow;
        var split = Math.Max(4, summary.LastCol / 2);

        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            UppercaseDataBarIds(document);

            if (hasDepots)
            {
                AddExecutiveClusteredColumnChart(
                    document,
                    targetSheetName: "Summary",
                    chartName: "Sales by depot",
                    sourceSheetName: "Summary",
                    headerRow: summary.Depots.HeaderRow,
                    categoryColumn: 1,
                    dataStartRow: summary.Depots.FirstRow,
                    dataEndRow: summary.Depots.LastRow,
                    seriesColumns: [summary.SalesColumn, summary.PreviousColumn],
                    seriesColors: ["2563EB", "94A3B8"],
                    fromColumn: 0,
                    fromRow: fromRow,
                    toColumn: hasProducts ? split : summary.LastCol,
                    toRow: toRow);
            }

            if (hasProducts)
            {
                AddExecutiveClusteredColumnChart(
                    document,
                    targetSheetName: "Summary",
                    chartName: "Ten best-selling items",
                    sourceSheetName: "Products",
                    headerRow: products.Table.HeaderRow,
                    categoryColumn: products.DescriptionColumn,
                    dataStartRow: products.Table.FirstRow,
                    dataEndRow: Math.Min(products.Table.LastRow, products.Table.FirstRow + 9),
                    seriesColumns: [products.ValueColumn],
                    seriesColors: ["10B981"],
                    fromColumn: hasDepots ? split : 0,
                    fromRow: fromRow,
                    toColumn: summary.LastCol,
                    toRow: toRow);
            }
        }

        return stream.ToArray();
    }

    private static void UppercaseDataBarIds(SpreadsheetDocument document, IReadOnlyCollection<string>? sheetNames = null)
    {
        var parts = sheetNames is null || document.WorkbookPart is not { } workbookPart
            ? document.WorkbookPart?.WorksheetParts ?? []
            : sheetNames.Select(name => GetWorksheetPartByName(workbookPart, name)).OfType<WorksheetPart>();

        foreach (var part in parts)
        {
            var changed = false;

            foreach (var rule in part.Worksheet.Descendants<DocumentFormat.OpenXml.Office2010.Excel.ConditionalFormattingRule>())
            {
                if (rule.Id?.Value is { } id && id != id.ToUpperInvariant())
                {
                    rule.Id = id.ToUpperInvariant();
                    changed = true;
                }
            }

            foreach (var reference in part.Worksheet.Descendants<DocumentFormat.OpenXml.Office2010.Excel.Id>())
            {
                if (reference.Text is { } id && id != id.ToUpperInvariant())
                {
                    reference.Text = id.ToUpperInvariant();
                    changed = true;
                }
            }

            if (changed)
            {
                part.Worksheet.Save();
            }
        }
    }
}
