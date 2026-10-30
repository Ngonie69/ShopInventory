using ClosedXML.Excel;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The order conversion workbook: what the sales reps and the ADRs ordered, followed to the van sales
/// it became.
/// </summary>
/// <remarks>
/// Summary leads — the headline, the split by who raised the order, where the vans' sales came from,
/// the areas to look at and the projection — then one filterable table per sheet: Reps, Products,
/// Shops and Order Ledger. Money is a number beside a currency column, never text, and is never summed
/// across currencies: the detail sheets carry no grand total for that reason, and Summary gives one
/// row per currency.
/// </remarks>
public partial class ReportExportService
{
    private static readonly XLColor OrderSalesRepTint = XLColor.FromHtml("#E3F2FD");
    private static readonly XLColor OrderAdrTint = XLColor.FromHtml("#F3E5F5");

    public byte[] ExportOrderConversionToExcel(OrderConversionReportResponse report)
    {
        using var workbook = NewWorkbook("Order Conversion");
        var names = report.Reps.ToDictionary(rep => rep.UserId);

        BuildOrderConversionSummary(workbook, report);
        BuildOrderConversionReps(workbook, report);
        BuildOrderConversionProducts(workbook, report);
        BuildOrderConversionShops(workbook, report);
        BuildOrderConversionLedger(workbook, report, names);

        workbook.Worksheet("Summary").SetTabActive();
        return WorkbookToBytes(workbook);
    }

    private static void BuildOrderConversionSummary(XLWorkbook workbook, OrderConversionReportResponse report)
    {
        const int columns = 9;
        var ws = AddSheet(workbook, "Summary", NavyBlue);
        var lead = report.LeadCurrency;
        var money = report.Overall.In(lead);

        var row = WriteReportHeader(ws, "Order Conversion — Sales Orders to Actual Sales", columns, report.FromDate, report.ToDate,
            subtitle: "Sales reps' and ADRs' van orders followed to the van invoice each became · money includes VAT, per currency");

        WriteKpiCard(ws, row, 1, $"Ordered ({lead})", money?.Ordered ?? 0m, FormatMoney);
        WriteKpiCard(ws, row, 2, $"Actual sales ({lead})", money?.Invoiced ?? 0m, FormatMoney, SuccessGreen);
        WriteKpiCard(ws, row, 3, "Value conversion", money?.Conversion is { } conversion ? conversion.ToString("P1") : "—");
        WriteKpiCard(ws, row, 4, "Product conversion", report.Products.UnitConversion is { } units ? units.ToString("P1") : "—");
        WriteKpiCard(ws, row, 5, $"Not converted ({lead})", money?.Gap ?? 0m, FormatMoney, WarningOrange);
        WriteKpiCard(ws, row, 6, "Orders", report.Overall.Counts.Ordered, FormatCount);
        row += 3;

        // ── By who raised the order ──
        row = OrderSection(ws, row, columns, "By who raised the order");
        string[] raiserHeaders = ["Raised by", "Currency", "Reps ordering", "Orders", "Ordered", "Actual sales", "Not converted", "Conversion", "Fill rate"];
        OrderHeaders(ws, row, raiserHeaders);
        StyleTableHeader(ws, row, columns);
        row++;
        foreach (var raiser in report.Raisers)
        {
            foreach (var part in raiser.Summary.Money)
            {
                ws.Cell(row, 1).Value = raiser.Label;
                ws.Cell(row, 2).Value = part.Currency;
                ws.Cell(row, 3).Value = raiser.ActivePeople;
                ws.Cell(row, 4).Value = raiser.Summary.Counts.Ordered;
                OrderMoneyCell(ws.Cell(row, 5), part.Ordered);
                OrderMoneyCell(ws.Cell(row, 6), part.Invoiced);
                OrderMoneyCell(ws.Cell(row, 7), part.Gap);
                OrderRateCell(ws.Cell(row, 8), part.Conversion);
                OrderRateCell(ws.Cell(row, 9), part.FillRate);
                ws.Range(row, 1, row, columns).Style.Fill.BackgroundColor =
                    raiser.Role == OrderRaiser.Adrs ? OrderAdrTint : OrderSalesRepTint;
                row++;
            }
        }

        row++;

        // ── Where the vans' sales came from ──
        row = OrderSection(ws, row, columns, "Every van sale in the period, by where it came from");
        string[] mixHeaders = ["Currency", "Van sales", "From sales reps' orders", "From ADRs' orders", "From other orders", "Sold on the spot", "From orders", "Sales", "From orders (count)"];
        OrderHeaders(ws, row, mixHeaders);
        StyleTableHeader(ws, row, columns);
        row++;
        foreach (var mix in report.SalesMix)
        {
            ws.Cell(row, 1).Value = mix.Currency;
            OrderMoneyCell(ws.Cell(row, 2), mix.VanSales);
            OrderMoneyCell(ws.Cell(row, 3), mix.FromSalesRepOrders);
            OrderMoneyCell(ws.Cell(row, 4), mix.FromAdrOrders);
            OrderMoneyCell(ws.Cell(row, 5), mix.FromOtherOrders);
            OrderMoneyCell(ws.Cell(row, 6), mix.Direct);
            OrderRateCell(ws.Cell(row, 7), mix.FromOrdersShare);
            ws.Cell(row, 8).Value = mix.SaleCount;
            ws.Cell(row, 9).Value = mix.FromOrdersCount;
            row++;
        }

        row++;

        // ── Areas to look at ──
        row = OrderSection(ws, row, columns, "Areas to look at");
        string[] attentionHeaders = ["Severity", "Area", "Finding", "Currency", "Value at stake", "What it means"];
        OrderHeaders(ws, row, attentionHeaders);
        StyleTableHeader(ws, row, columns);
        row++;
        if (report.Attention.Count == 0)
        {
            ws.Cell(row++, 1).Value = "Nothing stands out.";
        }

        foreach (var item in report.Attention)
        {
            ws.Cell(row, 1).Value = item.Severity.ToUpperInvariant();
            ws.Cell(row, 1).Style.Font.FontColor = item.Severity switch
            {
                "high" => DangerRed,
                "medium" => WarningOrange,
                _ => NavyBlue
            };
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = item.Area;
            ws.Cell(row, 3).Value = item.Title;
            ws.Cell(row, 4).Value = item.Currency ?? string.Empty;
            if (item.ValueAtStake is { } value)
            {
                OrderMoneyCell(ws.Cell(row, 5), value);
            }

            ws.Range(row, 6, row, columns).Merge();
            ws.Cell(row, 6).Value = item.Detail;
            ws.Cell(row, 6).Style.Alignment.WrapText = true;
            ws.Row(row).Height = 30;
            row++;
        }

        row++;

        // ── Projection ──
        if (report.Projections.FirstOrDefault(part => string.Equals(part.Currency, lead, StringComparison.OrdinalIgnoreCase)) is { } projection)
        {
            row = OrderSection(ws, row, columns, $"Projection ({projection.Currency}) — at this period's pace");
            (string Label, object Value, string Format)[] lines =
            [
                ("Ordered a trading day", projection.OrderedPerDay, FormatMoney),
                ("Converted a trading day", projection.ConvertedPerDay, FormatMoney),
                ("Conversion of settled orders (two days or older)", projection.SettledConversion ?? 0d, FormatPercent),
                ("Orders still open", projection.OpenOrdersValue, FormatMoney),
                ("Expected from open orders", projection.ExpectedFromOpenOrders, FormatMoney),
                ("This period's conversion once settled", projection.ProjectedPeriodConversion ?? 0d, FormatPercent),
                ($"Projected ordered — {projection.HorizonLabel} ({projection.HorizonDays} trading days)", projection.ProjectedOrdered, FormatMoney),
                ($"Projected actual sales — {projection.HorizonLabel}", projection.ProjectedConverted, FormatMoney),
                ($"Projected not converted — {projection.HorizonLabel}", projection.ProjectedLeakage, FormatMoney),
                ($"Extra sales at {projection.TargetConversion:P0} conversion", projection.UpsideAtTarget, FormatMoney)
            ];

            foreach (var (label, value, format) in lines)
            {
                ws.Range(row, 1, row, 4).Merge();
                ws.Cell(row, 1).Value = label;
                ws.Cell(row, 5).Value = value switch
                {
                    decimal amount => amount,
                    double rate => rate,
                    _ => value.ToString()
                };
                ws.Cell(row, 5).Style.NumberFormat.Format = format;
                row++;
            }

            if (projection.ProjectedMonthOrdered is { } monthOrdered)
            {
                ws.Range(row, 1, row, 4).Merge();
                ws.Cell(row, 1).Value = "Month end — ordered / actual sales";
                OrderMoneyCell(ws.Cell(row, 5), monthOrdered);
                OrderMoneyCell(ws.Cell(row, 6), projection.ProjectedMonthConverted ?? 0m);
                row++;
            }

            if (projection.WeeklyTrendPoints is { } slope)
            {
                ws.Range(row, 1, row, 4).Merge();
                ws.Cell(row, 1).Value = "Weekly trend (percentage points a week)";
                ws.Cell(row, 5).Value = slope;
                ws.Cell(row, 5).Style.NumberFormat.Format = "+0.0;-0.0";
                row++;
            }

            row++;
            string[] weekHeaders = ["Week of", "Orders", "Ordered", "Actual sales", "Conversion"];
            OrderHeaders(ws, row, weekHeaders);
            StyleTableHeader(ws, row, weekHeaders.Length);
            row++;
            foreach (var week in projection.Weeks)
            {
                ws.Cell(row, 1).Value = week.WeekStart;
                ws.Cell(row, 1).Style.NumberFormat.Format = FormatDate;
                ws.Cell(row, 2).Value = week.Orders;
                OrderMoneyCell(ws.Cell(row, 3), week.Ordered);
                OrderMoneyCell(ws.Cell(row, 4), week.Invoiced);
                OrderRateCell(ws.Cell(row, 5), week.Conversion);
                row++;
            }

            row++;
        }

        if (report.Caveats.Count > 0)
        {
            row = OrderSection(ws, row, columns, "What this period could not answer");
            foreach (var caveat in report.Caveats)
            {
                ws.Range(row, 1, row, columns).Merge();
                ws.Cell(row, 1).Value = caveat;
                ws.Cell(row, 1).Style.Alignment.WrapText = true;
                ws.Row(row).Height = 28;
                row++;
            }
        }

        WriteFooter(ws, row, columns);
        FinalizeSheet(ws, columns, landscape: true, fitFromRow: 8);
    }

    private static void BuildOrderConversionReps(XLWorkbook workbook, OrderConversionReportResponse report)
    {
        string[] headers =
        [
            "Rep", "Username", "Raised by", "Route", "Van account", "Currency", "Orders", "Invoiced orders",
            "In full", "Short-supplied", "No linked invoice", "Not yet in SAP", "Cancelled",
            "Ordered", "Actual sales", "Not converted", "Short value", "Conversion", "Fill rate", "Median days to invoice"
        ];
        var ws = AddSheet(workbook, "Reps", NavyBlue);
        var row = WriteReportHeader(ws, "Order Conversion — by Rep", headers.Length, report.FromDate, report.ToDate);
        OrderHeaders(ws, row, headers);
        StyleTableHeader(ws, row, headers.Length);
        var headerRow = row++;
        var first = row;

        foreach (var rep in report.Reps)
        {
            foreach (var part in rep.Summary.Money.DefaultIfEmpty(new AdrConversionMoney { Currency = report.LeadCurrency ?? "" }))
            {
                var counts = rep.Summary.Counts;
                ws.Cell(row, 1).Value = rep.DisplayName;
                ws.Cell(row, 2).Value = rep.Username;
                ws.Cell(row, 3).Value = OrderRaiser.Singular(rep.Role);
                ws.Cell(row, 4).Value = rep.RouteCode is null ? string.Empty : $"{rep.RouteCode} {rep.RouteName}";
                ws.Cell(row, 5).Value = rep.VanAccountCode ?? string.Empty;
                ws.Cell(row, 6).Value = part.Currency;
                ws.Cell(row, 7).Value = counts.Ordered;
                ws.Cell(row, 8).Value = counts.Invoiced;
                ws.Cell(row, 9).Value = counts.InvoicedInFull;
                ws.Cell(row, 10).Value = counts.ShortSupplied;
                ws.Cell(row, 11).Value = counts.NoLinkedInvoice;
                ws.Cell(row, 12).Value = counts.NotYetInSap;
                ws.Cell(row, 13).Value = counts.Cancelled;
                OrderMoneyCell(ws.Cell(row, 14), part.Ordered);
                OrderMoneyCell(ws.Cell(row, 15), part.Invoiced);
                OrderMoneyCell(ws.Cell(row, 16), part.Gap);
                OrderMoneyCell(ws.Cell(row, 17), part.ShortSupplied);
                OrderRateCell(ws.Cell(row, 18), part.Conversion);
                OrderRateCell(ws.Cell(row, 19), part.FillRate);
                if (rep.Summary.MedianDaysToInvoice is { } days)
                {
                    ws.Cell(row, 20).Value = days;
                    ws.Cell(row, 20).Style.NumberFormat.Format = "0.0";
                }

                row++;
            }
        }

        row = FinishTable(ws, headerRow, first, row, headers.Length, "No rep raised an order in this period.");
        WriteFooter(ws, row, headers.Length);
        FinalizeSheet(ws, headers.Length, headerRow, landscape: true);
    }

    private static void BuildOrderConversionProducts(XLWorkbook workbook, OrderConversionReportResponse report)
    {
        string[] headers =
        [
            "Item", "Description", "Unit", "Orders", "Shops", "Ordered units", "Sales reps' units", "ADRs' units",
            "Delivered units", "Short units", "Units not invoiced", "Unit conversion",
            "Currency", "Ordered value", "Converted value", "Short value"
        ];
        var ws = AddSheet(workbook, "Products", NavyBlue);
        var row = WriteReportHeader(ws, "Order Conversion — by Product", headers.Length, report.FromDate, report.ToDate,
            subtitle: "Delivered units are on invoiced orders, against what each order asked for; units are in each order's own unit");
        OrderHeaders(ws, row, headers);
        StyleTableHeader(ws, row, headers.Length);
        var headerRow = row++;
        var first = row;

        foreach (var item in report.Products.Items)
        {
            foreach (var part in item.Money.DefaultIfEmpty(new OrderConversionItemMoney { Currency = report.LeadCurrency ?? "" }))
            {
                ws.Cell(row, 1).Value = item.ItemCode;
                ws.Cell(row, 2).Value = item.ItemDescription ?? string.Empty;
                ws.Cell(row, 3).Value = item.UoMCode ?? string.Empty;
                ws.Cell(row, 4).Value = item.Orders;
                ws.Cell(row, 5).Value = item.Shops;
                OrderQuantityCell(ws.Cell(row, 6), item.OrderedQuantity);
                OrderQuantityCell(ws.Cell(row, 7), item.SalesRepQuantity);
                OrderQuantityCell(ws.Cell(row, 8), item.AdrQuantity);
                OrderQuantityCell(ws.Cell(row, 9), item.SuppliedQuantity);
                OrderQuantityCell(ws.Cell(row, 10), item.ShortQuantity);
                OrderQuantityCell(ws.Cell(row, 11), item.NotInvoicedQuantity);
                OrderRateCell(ws.Cell(row, 12), item.UnitConversion);
                ws.Cell(row, 13).Value = part.Currency;
                OrderMoneyCell(ws.Cell(row, 14), part.Ordered);
                OrderMoneyCell(ws.Cell(row, 15), part.Converted);
                OrderMoneyCell(ws.Cell(row, 16), part.ShortSupplied);
                row++;
            }
        }

        row = FinishTable(ws, headerRow, first, row, headers.Length, "No products were ordered in this period.", stripeByRule: true);
        WriteFooter(ws, row, headers.Length);
        FinalizeSheet(ws, headers.Length, headerRow, landscape: true, fitRows: 300);
    }

    private static void BuildOrderConversionShops(XLWorkbook workbook, OrderConversionReportResponse report)
    {
        string[] headers =
        [
            "Shop", "Code", "Ordered by", "Orders", "Invoiced orders", "Short-supplied", "Not invoiced",
            "Currency", "Ordered", "Actual sales", "Short value", "Conversion", "Last order"
        ];
        var ws = AddSheet(workbook, "Shops", NavyBlue);
        var row = WriteReportHeader(ws, "Order Conversion — by Shop", headers.Length, report.FromDate, report.ToDate);
        OrderHeaders(ws, row, headers);
        StyleTableHeader(ws, row, headers.Length);
        var headerRow = row++;
        var first = row;

        foreach (var shop in report.Shops)
        {
            foreach (var part in shop.Money)
            {
                ws.Cell(row, 1).Value = shop.CustomerName ?? string.Empty;
                ws.Cell(row, 2).Value = shop.CustomerCode;
                ws.Cell(row, 3).Value = shop.RaisedBy;
                ws.Cell(row, 4).Value = shop.Orders;
                ws.Cell(row, 5).Value = shop.Invoiced;
                ws.Cell(row, 6).Value = shop.ShortSupplied;
                ws.Cell(row, 7).Value = shop.Uninvoiced;
                ws.Cell(row, 8).Value = part.Currency;
                OrderMoneyCell(ws.Cell(row, 9), part.Ordered);
                OrderMoneyCell(ws.Cell(row, 10), part.Invoiced);
                OrderMoneyCell(ws.Cell(row, 11), part.ShortSupplied);
                OrderRateCell(ws.Cell(row, 12), part.Conversion);
                if (shop.LastOrderOn is { } last)
                {
                    ws.Cell(row, 13).Value = last;
                    ws.Cell(row, 13).Style.NumberFormat.Format = FormatDate;
                }

                row++;
            }
        }

        row = FinishTable(ws, headerRow, first, row, headers.Length, "No shop was recorded on any order.", stripeByRule: true);
        WriteFooter(ws, row, headers.Length);
        FinalizeSheet(ws, headers.Length, headerRow, landscape: true, fitRows: 300);
    }

    private static void BuildOrderConversionLedger(
        XLWorkbook workbook,
        OrderConversionReportResponse report,
        Dictionary<Guid, OrderConversionRep> names)
    {
        string[] headers =
        [
            "Captured", "Order", "Raised by", "Rep", "Shop", "Currency", "Ordered", "SAP order",
            "Invoice", "Invoice reference", "Invoiced at", "Invoiced", "Days to invoice", "Variance",
            "Short value", "Outcome"
        ];
        var ws = AddSheet(workbook, "Order Ledger", NavyBlue);
        var subtitle = report.OrdersNotListed > 0
            ? $"The newest {report.Orders.Count:N0} orders; {report.OrdersNotListed:N0} older ones are counted in every figure but not listed"
            : null;
        var row = WriteReportHeader(ws, "Order Conversion — Order Ledger", headers.Length, report.FromDate, report.ToDate, subtitle: subtitle);
        OrderHeaders(ws, row, headers);
        StyleTableHeader(ws, row, headers.Length);
        var headerRow = row++;
        var first = row;

        foreach (var entry in report.Orders)
        {
            var order = entry.Order;
            ws.Cell(row, 1).Value = order.CapturedAtCat;
            ws.Cell(row, 1).Style.NumberFormat.Format = FormatTimestamp;
            ws.Cell(row, 2).Value = order.OrderNumber;
            ws.Cell(row, 3).Value = OrderRaiser.Singular(entry.Role);
            ws.Cell(row, 4).Value = names.TryGetValue(order.UserId, out var rep) ? rep.DisplayName : string.Empty;
            ws.Cell(row, 5).Value = order.DisplayCustomer;
            ws.Cell(row, 6).Value = order.Currency;
            OrderMoneyCell(ws.Cell(row, 7), order.Ordered);
            if (order.SapOrderDocNum is { } sapOrder)
            {
                ws.Cell(row, 8).Value = sapOrder;
            }

            if (order.InvoiceDocNum is { } invoice)
            {
                ws.Cell(row, 9).Value = invoice;
            }

            ws.Cell(row, 10).Value = order.InvoiceReference ?? string.Empty;
            if (order.InvoicedAtCat is { } invoicedAt)
            {
                ws.Cell(row, 11).Value = invoicedAt;
                ws.Cell(row, 11).Style.NumberFormat.Format = FormatTimestamp;
            }

            if (order.Invoiced is { } invoiced)
            {
                OrderMoneyCell(ws.Cell(row, 12), invoiced);
            }

            if (order.DaysToInvoice is { } days)
            {
                ws.Cell(row, 13).Value = days;
                ws.Cell(row, 13).Style.NumberFormat.Format = "0.0";
            }

            if (order.Variance is { } variance)
            {
                OrderMoneyCell(ws.Cell(row, 14), variance);
            }

            OrderMoneyCell(ws.Cell(row, 15), order.ShortSupplied);
            ws.Cell(row, 16).Value = order.Outcome;
            row++;
        }

        row = FinishTable(ws, headerRow, first, row, headers.Length, "No orders in this period.", stripeByRule: true);
        WriteFooter(ws, row, headers.Length);
        FinalizeSheet(ws, headers.Length, headerRow, landscape: true, fitRows: 300);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static void OrderHeaders(IXLWorksheet ws, int row, IReadOnlyList<string> headers)
    {
        for (var col = 0; col < headers.Count; col++)
        {
            ws.Cell(row, col + 1).Value = headers[col];
        }
    }

    private static int OrderSection(IXLWorksheet ws, int row, int columns, string title)
    {
        ws.Range(row, 1, row, columns).Merge();
        ws.Cell(row, 1).Value = title;
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 1).Style.Font.FontSize = 12;
        ws.Cell(row, 1).Style.Font.FontColor = NavyBlue;
        ws.Range(row, 1, row, columns).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        ws.Range(row, 1, row, columns).Style.Border.BottomBorderColor = NavyBlue;
        return row + 1;
    }

    private static void OrderMoneyCell(IXLCell cell, decimal value)
    {
        cell.Value = value;
        cell.Style.NumberFormat.Format = FormatMoney;
    }

    private static void OrderQuantityCell(IXLCell cell, decimal value)
    {
        cell.Value = value;
        cell.Style.NumberFormat.Format = FormatQuantity;
    }

    /// <summary>A rate, or an empty cell where there is no denominator — never a zero standing in for one.</summary>
    private static void OrderRateCell(IXLCell cell, double? rate)
    {
        if (rate is { } value)
        {
            cell.Value = value;
            cell.Style.NumberFormat.Format = FormatPercent;
        }
    }
}
