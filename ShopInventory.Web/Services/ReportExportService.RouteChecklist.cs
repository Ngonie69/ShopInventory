using System.Globalization;
using ClosedXML.Excel;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>What the route checklist on Retail Sales Orders hands the workbook.</summary>
/// <param name="Day">The day the checklist is for.</param>
/// <param name="MatchedOnDelivery">Orders were matched on delivery date rather than order date.</param>
/// <param name="RoutesLabel">Which routes are in it, as the page's filter reads: "All routes", "Tuesday routes".</param>
/// <param name="Search">The search on screen, when there is one.</param>
/// <param name="Routes">The routes on screen, each with every one of its shops.</param>
/// <param name="OffRouteOrders">Orders from shops on no route.</param>
/// <param name="SkippedOrderCount">Cancelled or rejected orders, which tick nothing.</param>
/// <param name="StatusName">The page's name for an order's status.</param>
public sealed record RouteChecklistExport(
    DateTime Day,
    bool MatchedOnDelivery,
    string RoutesLabel,
    string? Search,
    IReadOnlyList<RouteChecklistRoute> Routes,
    IReadOnlyList<SalesOrderDto> OffRouteOrders,
    int SkippedOrderCount,
    Func<SalesOrderDto, string> StatusName);

/// <summary>
/// The route checklist workbook: for each route on screen, every shop marked ordered or not, with
/// the lines it ordered beneath it.
/// </summary>
/// <remarks>
/// <para>
/// The first sheet is written by hand rather than as a filtered table: it is grouped — a banner
/// per route, a block per shop — and that grouping is the point of it, so it is read top to bottom
/// like the page. The flat Order Lines sheet is the one to filter and pivot.
/// </para>
/// <para>
/// A shop on two of the chosen routes appears under both on the Checklist, since each route is
/// read on its own. Order Lines lists each order once, with every route it is on, so its
/// per-currency totals do not count an order twice.
/// </para>
/// </remarks>
public partial class ReportExportService
{
    private static readonly XLColor ChecklistOrderedFill = XLColor.FromHtml("#e8f5e9");
    private static readonly XLColor ChecklistOrderedBadge = XLColor.FromHtml("#c8e6c9");
    private static readonly XLColor ChecklistMissingFill = XLColor.FromHtml("#fdecea");
    private static readonly XLColor ChecklistMissingBadge = XLColor.FromHtml("#f8c9c4");
    private static readonly XLColor ChecklistOffRouteBanner = XLColor.FromHtml("#e65100");

    public byte[] ExportRouteChecklistToExcel(RouteChecklistExport export)
    {
        var title = $"Route Checklist — {export.Day:ddd d MMM yyyy}";
        var shops = export.Routes.Sum(route => route.Shops.Count);
        var ordered = export.Routes.Sum(route => route.OrderedCount);
        var subtitle = string.Join(" · ", new[]
        {
            export.MatchedOnDelivery ? "Orders for delivery this day" : "Orders placed this day",
            export.RoutesLabel,
            export.Search is { Length: > 0 } search ? $"matching \"{search}\"" : null,
            $"{ordered:N0} of {shops:N0} shop stops ordered",
            export.SkippedOrderCount > 0 ? $"{export.SkippedOrderCount:N0} cancelled or rejected not counted" : null
        }.Where(part => part is not null));

        using var workbook = NewWorkbook(title);
        WriteChecklistSheet(workbook, export, title, subtitle, shops, ordered);
        WriteChecklistSummarySheet(workbook, export, subtitle);
        WriteChecklistLinesSheet(workbook, export, subtitle);
        WriteChecklistMissingSheet(workbook, export, subtitle);
        return WorkbookToBytes(workbook);
    }

    private static void WriteChecklistSheet(
        XLWorkbook workbook, RouteChecklistExport export, string title, string subtitle, int shops, int ordered)
    {
        const int columns = 13;
        var ws = AddSheet(workbook, "Checklist", NavyBlue);
        var row = WriteReportHeader(ws, title, columns, subtitle: subtitle);

        var orderCount = export.Routes.SelectMany(route => route.Shops).SelectMany(shop => shop.Orders)
            .Select(order => order.Id).Distinct().Count();
        WriteKpiCard(ws, row, 1, "Shops ordered", ordered, FormatCount, SuccessGreen);
        WriteKpiCard(ws, row, 3, "No order", shops - ordered, FormatCount, shops - ordered > 0 ? DangerRed : null);
        WriteKpiCard(ws, row, 5, "Orders", orderCount, FormatCount);
        WriteKpiCard(ws, row, 7, "Orders off route", export.OffRouteOrders.Count, FormatCount,
            export.OffRouteOrders.Count > 0 ? WarningOrange : null);
        row += 3;

        string[] headers =
        [
            "Shop", "Codes", "Ordered?", "Order #", "Status", "Placed by", "Item", "Description",
            "Qty", "UoM", "Line total", "Ccy", "Order total"
        ];
        for (var col = 0; col < headers.Length; col++)
        {
            ws.Cell(row, col + 1).Value = headers[col];
        }

        StyleTableHeader(ws, row, columns);
        var headerRow = row;
        row++;

        if (export.Routes.Count == 0 && export.OffRouteOrders.Count == 0)
        {
            row = FinishTable(ws, headerRow, row, row, columns, "No route matches this checklist's filters.", filter: false);
        }

        foreach (var route in export.Routes)
        {
            var meta = route.Route.Days.Count == 0 ? "No day set" : string.Join(" / ", route.Route.Days);
            if (!string.IsNullOrWhiteSpace(route.Route.Truck))
            {
                meta += $" · {route.Route.Truck}";
            }

            row = WriteChecklistBanner(ws, row, columns,
                $"{route.Route.Name}   ·   {meta}   ·   {route.OrderedCount} of {route.Shops.Count} shops ordered",
                NavyBlue);

            foreach (var shop in route.Shops)
            {
                row = WriteChecklistShop(ws, row, columns, shop.Name, string.Join(", ", shop.CardCodes),
                    shop.IsOnHold, shop.Orders, export.StatusName);
            }
        }

        if (export.OffRouteOrders.Count > 0)
        {
            row = WriteChecklistBanner(ws, row, columns,
                $"SHOPS ON NO ROUTE   ·   {export.OffRouteOrders.Count} {(export.OffRouteOrders.Count == 1 ? "order" : "orders")} — put these shops on a route in Delivery Routes",
                ChecklistOffRouteBanner);

            foreach (var group in export.OffRouteOrders.GroupBy(order => order.CardCode.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                row = WriteChecklistShop(ws, row, columns, first.CardName ?? first.CardCode, first.CardCode,
                    isOnHold: false, group.ToList(), export.StatusName);
            }
        }

        WriteFooter(ws, row - 1, columns);
        FinalizeSheet(ws, columns, headerRow, landscape: true);
        ws.Column(1).Width = Math.Min(Math.Max(ws.Column(1).Width, 28), 46);
        ws.Column(8).Width = Math.Min(Math.Max(ws.Column(8).Width, 24), 42);
    }

    private static int WriteChecklistBanner(IXLWorksheet ws, int row, int columns, string text, XLColor fill)
    {
        var range = ws.Range(row, 1, row, columns);
        range.Merge();
        ws.Cell(row, 1).Value = text;
        range.Style.Fill.BackgroundColor = fill;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
        range.Style.Font.FontSize = 11;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.Indent = 1;
        ws.Row(row).Height = 22;
        return row + 1;
    }

    /// <summary>
    /// One shop's block: a single NO ORDER row, or a row per line of each order it placed, with
    /// the shop, its ORDERED badge and each order's header written on the block's first row only.
    /// </summary>
    private static int WriteChecklistShop(
        IXLWorksheet ws, int row, int columns, string name, string codes, bool isOnHold,
        IReadOnlyList<SalesOrderDto> orders, Func<SalesOrderDto, string> statusName)
    {
        var start = row;
        var hasOrdered = orders.Count > 0;

        ws.Cell(row, 1).Value = name;
        ws.Cell(row, 1).Style.Font.Bold = true;
        ws.Cell(row, 2).Value = isOnHold ? $"{codes} (on hold in SAP)" : codes;
        ws.Cell(row, 3).Value = hasOrdered ? "ORDERED" : "NO ORDER";

        if (!hasOrdered)
        {
            row++;
        }

        foreach (var order in orders)
        {
            var lines = order.Lines ?? [];
            var orderStart = row;
            ws.Cell(row, 4).Value = order.OrderNumber;
            ws.Cell(row, 4).Style.Font.Bold = true;
            ws.Cell(row, 5).Value = statusName(order);
            ws.Cell(row, 6).Value = order.CreatedByUserName ?? string.Empty;
            ws.Cell(row, 13).Value = order.DocTotal;
            ws.Cell(row, 13).Style.NumberFormat.Format = FormatMoney;
            ws.Cell(row, 13).Style.Font.Bold = true;

            if (lines.Count == 0)
            {
                ws.Cell(row, 8).Value = "No lines on this order";
                ws.Cell(row, 8).Style.Font.Italic = true;
                ws.Cell(row, 8).Style.Font.FontColor = MutedText;
                ws.Cell(row, 12).Value = order.Currency ?? string.Empty;
                row++;
            }

            foreach (var line in lines.OrderBy(line => line.LineNum))
            {
                ws.Cell(row, 7).Value = line.ItemCode;
                ws.Cell(row, 8).Value = line.ItemDescription ?? string.Empty;
                ws.Cell(row, 9).Value = line.Quantity;
                ws.Cell(row, 9).Style.NumberFormat.Format = FormatQuantity;
                ws.Cell(row, 10).Value = line.UoMCode ?? string.Empty;
                ws.Cell(row, 11).Value = line.LineTotal;
                ws.Cell(row, 11).Style.NumberFormat.Format = FormatMoney;
                ws.Cell(row, 12).Value = order.Currency ?? string.Empty;
                row++;
            }

            // A thin rule between two orders from one shop, so their lines are not read as one.
            if (orderStart > start)
            {
                ws.Range(orderStart, 4, orderStart, columns).Style.Border.TopBorder = XLBorderStyleValues.Thin;
                ws.Range(orderStart, 4, orderStart, columns).Style.Border.TopBorderColor = BorderGray;
            }
        }

        var block = ws.Range(start, 1, row - 1, columns);
        block.Style.Fill.BackgroundColor = hasOrdered ? ChecklistOrderedFill : ChecklistMissingFill;
        block.Style.Font.FontSize = 10;
        block.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        block.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        block.Style.Border.OutsideBorderColor = MedGray;

        var badge = ws.Cell(start, 3);
        badge.Style.Fill.BackgroundColor = hasOrdered ? ChecklistOrderedBadge : ChecklistMissingBadge;
        badge.Style.Font.Bold = true;
        badge.Style.Font.FontColor = hasOrdered ? SuccessGreen : DangerRed;
        badge.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        if (isOnHold)
        {
            ws.Cell(start, 2).Style.Font.FontColor = DangerRed;
        }

        return row;
    }

    private static void WriteChecklistSummarySheet(XLWorkbook workbook, RouteChecklistExport export, string subtitle)
    {
        var currencies = export.Routes
            .SelectMany(route => route.Shops).SelectMany(shop => shop.Orders)
            .Concat(export.OffRouteOrders)
            .Select(CurrencyOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(currency => currency, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var columns = 8 + currencies.Count;

        var ws = AddSheet(workbook, "Route Summary", NavyBlue);
        var row = WriteReportHeader(ws, "Route Checklist by Route", columns, subtitle: subtitle);

        var headers = new List<string> { "Route", "Days", "Truck", "Shops", "Ordered", "No order", "Coverage", "Orders" };
        headers.AddRange(currencies.Select(currency => $"Value {currency}"));
        for (var col = 0; col < headers.Count; col++)
        {
            ws.Cell(row, col + 1).Value = headers[col];
        }

        StyleTableHeader(ws, row, columns);
        var headerRow = row;
        row++;
        var dataStart = row;

        foreach (var route in export.Routes)
        {
            var orders = route.Shops.SelectMany(shop => shop.Orders).ToList();
            ws.Cell(row, 1).Value = route.Route.Name;
            ws.Cell(row, 2).Value = route.Route.Days.Count == 0 ? "No day set" : string.Join(", ", route.Route.Days);
            ws.Cell(row, 3).Value = route.Route.Truck ?? string.Empty;
            ws.Cell(row, 4).Value = route.Shops.Count;
            ws.Cell(row, 5).Value = route.OrderedCount;
            ws.Cell(row, 6).Value = route.Shops.Count - route.OrderedCount;
            ws.Cell(row, 7).Value = route.Shops.Count == 0 ? 0 : (double)route.OrderedCount / route.Shops.Count;
            ws.Cell(row, 7).Style.NumberFormat.Format = "0%";
            ws.Cell(row, 8).Value = orders.Count;
            for (var c = 4; c <= 8; c++)
            {
                if (c != 7)
                {
                    ws.Cell(row, c).Style.NumberFormat.Format = FormatCount;
                }
            }

            for (var i = 0; i < currencies.Count; i++)
            {
                ws.Cell(row, 9 + i).Value = orders
                    .Where(order => string.Equals(CurrencyOf(order), currencies[i], StringComparison.OrdinalIgnoreCase))
                    .Sum(order => order.DocTotal);
                ws.Cell(row, 9 + i).Style.NumberFormat.Format = FormatMoney;
            }

            row++;
        }

        var lastData = row - 1;
        var missingRows = Enumerable.Range(dataStart, Math.Max(0, lastData - dataStart + 1))
            .Where(r => ws.Cell(r, 6).GetValue<int>() > 0)
            .ToList();
        row = FinishTable(ws, headerRow, dataStart, row, columns, "No route matches this checklist's filters.");

        // After FinishTable, which stripes every row and would paint over these.
        foreach (var r in missingRows)
        {
            ws.Cell(r, 6).Style.Font.FontColor = DangerRed;
            ws.Cell(r, 6).Style.Font.Bold = true;
        }

        if (lastData >= dataStart)
        {
            ws.Cell(row, 1).Value = "TOTAL";
            WriteSubtotal(ws, row, 4, dataStart, lastData, FormatCount);
            WriteSubtotal(ws, row, 5, dataStart, lastData, FormatCount);
            WriteSubtotal(ws, row, 6, dataStart, lastData, FormatCount);
            ws.Cell(row, 7).FormulaA1 = $"IF(D{row}=0,0,E{row}/D{row})";
            ws.Cell(row, 7).Style.NumberFormat.Format = "0%";
            WriteSubtotal(ws, row, 8, dataStart, lastData, FormatCount);
            for (var i = 0; i < currencies.Count; i++)
            {
                WriteSubtotal(ws, row, 9 + i, dataStart, lastData, FormatMoney);
            }

            StyleTotalsRow(ws, row, columns);
            row++;
        }

        WriteFooter(ws, row - 1, columns);
        FinalizeSheet(ws, columns, headerRow, landscape: true);
    }

    private static void WriteChecklistLinesSheet(XLWorkbook workbook, RouteChecklistExport export, string subtitle)
    {
        const int columns = 15;
        var ws = AddSheet(workbook, "Order Lines", NavyBlue);
        var row = WriteReportHeader(ws, "Route Checklist — Order Lines", columns, subtitle: subtitle);

        string[] headers =
        [
            "Route", "Shop", "Code", "Order #", "Ordered", "Delivery", "Status", "Placed by",
            "Item", "Description", "Qty", "UoM", "Unit price", "Line total", "Ccy"
        ];
        for (var col = 0; col < headers.Length; col++)
        {
            ws.Cell(row, col + 1).Value = headers[col];
        }

        StyleTableHeader(ws, row, columns);
        var headerRow = row;
        row++;
        var dataStart = row;

        // Each order once, with every chosen route its shop is on.
        var routesByOrder = new Dictionary<int, List<string>>();
        var orders = new List<SalesOrderDto>();
        foreach (var route in export.Routes)
        {
            foreach (var order in route.Shops.SelectMany(shop => shop.Orders))
            {
                if (!routesByOrder.TryGetValue(order.Id, out var names))
                {
                    routesByOrder[order.Id] = names = [];
                    orders.Add(order);
                }

                if (!names.Contains(route.Route.Name))
                {
                    names.Add(route.Route.Name);
                }
            }
        }

        var written = new List<(SalesOrderDto Order, decimal Amount)>();
        foreach (var order in orders.Concat(export.OffRouteOrders))
        {
            var routeName = routesByOrder.TryGetValue(order.Id, out var names) ? string.Join(", ", names) : "(no route)";
            var lines = order.Lines is { Count: > 0 } orderLines
                ? orderLines.OrderBy(line => line.LineNum).Cast<SalesOrderLineDto?>().ToList()
                : [null];

            foreach (var line in lines)
            {
                ws.Cell(row, 1).Value = routeName;
                ws.Cell(row, 2).Value = order.CardName ?? string.Empty;
                ws.Cell(row, 3).Value = order.CardCode;
                ws.Cell(row, 4).Value = order.OrderNumber;
                ws.Cell(row, 5).Value = order.OrderDate;
                ws.Cell(row, 5).Style.DateFormat.Format = "dd MMM yyyy";
                if (order.DeliveryDate is { } delivery)
                {
                    ws.Cell(row, 6).Value = delivery;
                    ws.Cell(row, 6).Style.DateFormat.Format = "dd MMM yyyy";
                }

                ws.Cell(row, 7).Value = export.StatusName(order);
                ws.Cell(row, 8).Value = order.CreatedByUserName ?? string.Empty;
                ws.Cell(row, 15).Value = order.Currency ?? string.Empty;

                if (line is null)
                {
                    ws.Cell(row, 10).Value = "No lines on this order";
                    ws.Cell(row, 14).Value = order.DocTotal;
                    written.Add((order, order.DocTotal));
                }
                else
                {
                    ws.Cell(row, 9).Value = line.ItemCode;
                    ws.Cell(row, 10).Value = line.ItemDescription ?? string.Empty;
                    ws.Cell(row, 11).Value = line.Quantity;
                    ws.Cell(row, 11).Style.NumberFormat.Format = FormatQuantity;
                    ws.Cell(row, 12).Value = line.UoMCode ?? string.Empty;
                    ws.Cell(row, 13).Value = line.UnitPrice;
                    ws.Cell(row, 13).Style.NumberFormat.Format = FormatMoney;
                    ws.Cell(row, 14).Value = line.LineTotal;
                    written.Add((order, line.LineTotal));
                }

                ws.Cell(row, 14).Style.NumberFormat.Format = FormatMoney;
                row++;
            }
        }

        row = FinishTable(ws, headerRow, dataStart, row, columns, "No orders on these routes for this day.");
        row = WriteCurrencyTotals(
            ws, row, columns, currencyColumn: 15, labelColumn: 13,
            written.GroupBy(entry => CurrencyOf(entry.Order)),
            (sheet, totalRow, group) =>
            {
                var count = group.Select(entry => entry.Order.Id).Distinct().Count();
                sheet.Cell(totalRow, 13).Value = $"Total ({count:N0} {(count == 1 ? "order" : "orders")})";
                sheet.Cell(totalRow, 14).Value = group.Sum(entry => entry.Amount);
                sheet.Cell(totalRow, 14).Style.NumberFormat.Format = FormatMoney;
            });

        WriteFooter(ws, row - 1, columns);
        FinalizeSheet(ws, columns, headerRow, landscape: true);
    }

    private static void WriteChecklistMissingSheet(XLWorkbook workbook, RouteChecklistExport export, string subtitle)
    {
        const int columns = 5;
        var ws = AddSheet(workbook, "Not Ordered", DangerRed);
        var row = WriteReportHeader(ws, "Route Checklist — Shops With No Order", columns, subtitle: subtitle);

        string[] headers = ["Route", "Days", "Shop", "Codes", "On hold"];
        for (var col = 0; col < headers.Length; col++)
        {
            ws.Cell(row, col + 1).Value = headers[col];
        }

        StyleTableHeader(ws, row, columns);
        var headerRow = row;
        row++;
        var dataStart = row;

        foreach (var route in export.Routes)
        {
            foreach (var shop in route.Shops.Where(shop => !shop.HasOrdered))
            {
                ws.Cell(row, 1).Value = route.Route.Name;
                ws.Cell(row, 2).Value = route.Route.Days.Count == 0 ? "No day set" : string.Join(", ", route.Route.Days);
                ws.Cell(row, 3).Value = shop.Name;
                ws.Cell(row, 4).Value = string.Join(", ", shop.CardCodes);
                ws.Cell(row, 5).Value = shop.IsOnHold ? "Yes" : string.Empty;
                row++;
            }
        }

        row = FinishTable(ws, headerRow, dataStart, row, columns, "Every shop on these routes has ordered.");
        WriteFooter(ws, row - 1, columns);
        FinalizeSheet(ws, columns, headerRow);
    }

    private static string CurrencyOf(SalesOrderDto order) =>
        string.IsNullOrWhiteSpace(order.Currency) ? "-" : order.Currency.Trim().ToUpper(CultureInfo.InvariantCulture);
}
