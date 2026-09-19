using System.Globalization;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;
using ShopInventory.Common.Sales;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;
using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReviewPdf;

/// <summary>
/// Lays the business review out as an A4 document: each line of business in turn, its findings first,
/// then the figures behind them.
/// </summary>
/// <remarks>
/// <para>
/// Pure layout. Every number and every sentence comes from the review; nothing here decides what is
/// worth saying. The order is the order a manager reads in — what to act on first, then the evidence.
/// </para>
/// <para>
/// The standard Helvetica faces cover WinAnsi only, so the few typographic characters the findings use
/// outside it (arrows, the true minus) are mapped to plain equivalents rather than printing as gaps.
/// </para>
/// </remarks>
internal static class DesktopSalesReviewPdfRenderer
{
    private static readonly Color Ink = new DeviceRgb(15, 26, 34);
    private static readonly Color Muted = new DeviceRgb(96, 110, 120);
    private static readonly Color Rule = new DeviceRgb(221, 228, 232);
    private static readonly Color Accent = new DeviceRgb(29, 95, 138);
    private static readonly Color Band = new DeviceRgb(246, 248, 250);
    private static readonly Color ActionColor = new DeviceRgb(192, 49, 49);
    private static readonly Color ReviewColor = new DeviceRgb(178, 111, 0);
    private static readonly Color NoteColor = new DeviceRgb(29, 95, 138);

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static byte[] Render(DesktopSalesReview review, string title, DateTime generatedAtCat)
    {
        using var stream = new MemoryStream();
        var writer = new PdfWriter(stream);
        writer.SetCloseStream(false);
        var pdf = new PdfDocument(writer);
        pdf.GetDocumentInfo().SetTitle($"{title} {D(review.FromDate, "d MMM")} - {D(review.ToDate, "d MMM yyyy")}");

        var fonts = new Fonts(
            PdfFontFactory.CreateFont(StandardFonts.HELVETICA),
            PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD));

        var document = new Document(pdf, PageSize.A4, false);
        document.SetMargins(36, 36, 44, 36);
        document.SetFont(fonts.Regular).SetFontSize(9).SetFontColor(Ink);

        Header(document, fonts, review, title, generatedAtCat);
        Glance(document, fonts, review);

        if (review.Businesses.Count == 0)
        {
            document.Add(Text("No sales were recorded in this period.", fonts.Regular, 10).SetMarginTop(12));
        }

        // Each line of business on its own pages: shops and vending sell different ranges in different
        // ways, so nothing below adds them together.
        for (var i = 0; i < review.Businesses.Count; i++)
        {
            var business = review.Businesses[i];
            if (i > 0)
            {
                document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
            }

            document.Add(Text(business.Label.ToUpperInvariant(), fonts.Bold, 14)
                .SetFontColor(Accent)
                .SetCharacterSpacing(1f)
                .SetMarginTop(i == 0 ? 14 : 0)
                .SetBorderBottom(new SolidBorder(Accent, 1.5f))
                .SetPaddingBottom(3)
                .SetMarginBottom(6));
            Findings(document, fonts, business);

            foreach (var section in business.Currencies)
            {
                Currency(document, fonts, review, business, section);
            }

            Health(document, fonts, business);
        }

        Method(document, fonts, review);
        PageNumbers(document, pdf, fonts, title, review);

        document.Close();
        return stream.ToArray();
    }

    // ── Sections ───────────────────────────────────────────────────────────────────────────────────

    private static void Header(Document document, Fonts fonts, DesktopSalesReview review, string title, DateTime generatedAtCat)
    {
        document.Add(Text("DESKTOP SALES · BUSINESS REVIEW", fonts.Bold, 8).SetFontColor(Accent).SetCharacterSpacing(0.8f));
        document.Add(Text(title, fonts.Bold, 20).SetMarginTop(2));
        document.Add(Text(
                $"{D(review.FromDate, "ddd d MMM yyyy")} to {D(review.ToDate, "ddd d MMM yyyy")} · "
                + (review.WarehouseCode is null ? "all shops" : review.WarehouseCode)
                + $" · compared with {D(review.PreviousFromDate, "d MMM")} to {D(review.PreviousToDate, "d MMM")}",
                fonts.Regular, 10)
            .SetFontColor(Muted));
        document.Add(Text($"Generated {D(generatedAtCat, "d MMM yyyy, HH:mm")} CAT · online van receipts excluded (counted as their SAP invoices)", fonts.Regular, 8)
            .SetFontColor(Muted)
            .SetMarginBottom(10));
    }

    /// <summary>Each business's takings side by side, per currency, before each is read on its own.</summary>
    private static void Glance(Document document, Fonts fonts, DesktopSalesReview review)
    {
        if (review.Businesses.Count == 0)
        {
            return;
        }

        Heading(document, fonts, "By line of business", "Shops and vending are reviewed apart; currencies are never added together.");
        var table = Grid([28f, 12f, 14f, 22f, 24f]);
        Head(table, fonts, "Business", "Currency", "Sales", "Takings", "On the previous period");
        foreach (var business in review.Businesses)
        {
            foreach (var section in business.Currencies)
            {
                var h = section.Headline;
                Body(table, fonts, business.Label, bold: true);
                Body(table, fonts, section.Currency);
                Body(table, fonts, h.SalesCount.ToString("N0", Invariant), right: true);
                Body(table, fonts, Money(section.Currency, h.TotalAmount), right: true);
                Body(table, fonts, h.TotalChangePercent is { } change ? $"{Signed(change)}%" : "no previous period", right: true);
            }
        }

        document.Add(table);
    }

    private static void Findings(Document document, Fonts fonts, DesktopSalesReviewBusiness business)
    {
        Heading(document, fonts, "What the figures say", business.Findings.Count == 0 ? "Nothing stood out." : null);

        foreach (var finding in business.Findings)
        {
            var (label, color) = finding.Severity switch
            {
                DesktopSalesReviewSeverity.Action => ("ACT", ActionColor),
                DesktopSalesReviewSeverity.Review => ("CHECK", ReviewColor),
                _ => ("NOTE", NoteColor)
            };

            var row = new Table(UnitValue.CreatePercentArray([9f, 91f])).UseAllAvailableWidth().SetMarginBottom(4);
            row.AddCell(new Cell()
                .Add(Text(label, fonts.Bold, 7).SetFontColor(color))
                .SetBorder(Border.NO_BORDER)
                .SetBorderLeft(new SolidBorder(color, 2.5f))
                .SetPaddingLeft(6)
                .SetPaddingTop(3));
            row.AddCell(new Cell()
                .Add(Text(Clean(finding.Title), fonts.Bold, 9.5f))
                .Add(Text(Clean(finding.Detail), fonts.Regular, 8.5f).SetFontColor(Muted))
                .SetBorder(Border.NO_BORDER)
                .SetPaddingTop(2));
            document.Add(row);
        }
    }

    private static void Currency(
        Document document, Fonts fonts, DesktopSalesReview review, DesktopSalesReviewBusiness business, DesktopSalesReviewCurrency section)
    {
        var c = section.Currency;
        var h = section.Headline;

        document.Add(new AreaBreak(AreaBreakType.NEXT_PAGE));
        document.Add(Text($"{business.Label.ToUpperInvariant()} · {c} TRADING", fonts.Bold, 8).SetFontColor(Accent).SetCharacterSpacing(0.8f));
        document.Add(Text($"{Money(c, h.TotalAmount)} from {h.SalesCount:N0} sales", fonts.Bold, 16).SetMarginBottom(8));

        var tiles = new Table(UnitValue.CreatePercentArray([25f, 25f, 25f, 25f])).UseAllAvailableWidth().SetMarginBottom(12);
        Tile(tiles, fonts, "Takings", Money(c, h.TotalAmount),
            h.TotalChangePercent is { } change ? $"{Signed(change)}% on {Money(c, h.PreviousTotalAmount)}" : "no previous period");
        Tile(tiles, fonts, "Before VAT", Money(c, h.NetAmount), $"VAT {Money(c, h.VatAmount)} ({h.EffectiveVatPercent:0.00}%)");
        Tile(tiles, fonts, "Average sale", Money(c, h.AverageSale), $"{h.QuantitySold:N0} units · {h.DistinctItems:N0} items");
        Tile(tiles, fonts, "Gross margin",
            h.MarginPercent is { } margin ? $"{margin:0.#}%" : "—",
            h.GrossProfit is { } profit ? $"{Money(c, profit)} on {Money(c, h.CostedNetAmount)} costed" : business.Margin.Available ? "not posted to SAP yet" : "SAP unavailable");
        Tile(tiles, fonts, "Trading days", h.DaysTraded.ToString(Invariant), $"{D(review.FromDate, "d MMM")} to {D(review.ToDate, "d MMM")}");
        var cash = section.ByPaymentMethod.FirstOrDefault(m => m.PaymentMethod == "Cash");
        Tile(tiles, fonts, "Cash", cash is null ? "—" : $"{cash.ShareOfValuePercent:0.#}%", cash is null ? "no cash sales" : $"{Money(c, cash.TotalAmount)} of takings");
        Tile(tiles, fonts, "Short tendered", Money(c, h.ShortTendered), "sales accepted below total");
        Tile(tiles, fonts, "Traffic vs ticket",
            h.TrafficEffect is { } traffic && h.TicketEffect is { } ticket ? $"{SignedMoney(traffic)} / {SignedMoney(ticket)}" : "—",
            "more sales / bigger sales");
        document.Add(tiles);

        Days(document, fonts, section);
        Hours(document, fonts, business, section);
        Payments(document, fonts, section);
        Shops(document, fonts, section);
        Items(document, fonts, section);
        Spreads(document, fonts, section);
        LargeSales(document, fonts, section);
    }

    private static void Days(Document document, Fonts fonts, DesktopSalesReviewCurrency section)
    {
        if (section.ByDay.Count == 0)
        {
            return;
        }

        Heading(document, fonts, "Trading days", "Takings each day, split by how it was paid.");
        var methods = section.ByPaymentMethod.Select(m => m.PaymentMethod).ToList();
        var max = section.ByDay.Max(d => d.TotalAmount);

        var widths = new List<float> { 14f, 8f, 13f };
        widths.AddRange(methods.Select(_ => 11f));
        widths.Add(Math.Max(10f, 65f - 11f * methods.Count));
        var table = Grid(widths.ToArray());

        Head(table, fonts, "Day", "Sales", "Takings");
        foreach (var method in methods)
        {
            Head(table, fonts, method);
        }
        Head(table, fonts, "");

        foreach (var day in section.ByDay)
        {
            Body(table, fonts, D(day.Date, "ddd d MMM"));
            Body(table, fonts, day.SalesCount.ToString("N0", Invariant), right: true);
            Body(table, fonts, Number(day.TotalAmount), right: true, bold: true);
            foreach (var method in methods)
            {
                var amount = day.ByPaymentMethod.FirstOrDefault(p => p.PaymentMethod == method)?.TotalAmount ?? 0m;
                Body(table, fonts, amount == 0 ? "—" : Number(amount), right: true);
            }

            table.AddCell(BarCell(day.TotalAmount, max, Accent));
        }

        document.Add(table);
    }

    private static void Hours(Document document, Fonts fonts, DesktopSalesReviewBusiness business, DesktopSalesReviewCurrency section)
    {
        if (section.ByHour.Count == 0)
        {
            return;
        }

        Heading(document, fonts, "Hours of the day (CAT)",
            business.Business == SaleBusinesses.Vending ? "When settlements were captured: a vendor paying in, not goods selling." : null);

        var max = section.ByHour.Max(h => h.SalesCount);
        var table = Grid([10f, 12f, 16f, 62f]);
        Head(table, fonts, "Hour", "Sales", "Takings", "");

        foreach (var hour in section.ByHour)
        {
            Body(table, fonts, $"{hour.Hour:00}:00");
            Body(table, fonts, hour.SalesCount.ToString("N0", Invariant), right: true);
            Body(table, fonts, Number(hour.TotalAmount), right: true);
            table.AddCell(BarCell(hour.SalesCount, max, Accent));
        }

        document.Add(table);
    }

    private static void Payments(Document document, Fonts fonts, DesktopSalesReviewCurrency section)
    {
        Heading(document, fonts, "How it was paid", null);
        var table = Grid([22f, 12f, 18f, 14f, 14f, 20f]);
        Head(table, fonts, "Method", "Sales", "Takings", "Average", "Share", "No reference");

        foreach (var row in section.ByPaymentMethod)
        {
            Body(table, fonts, row.PaymentMethod);
            Body(table, fonts, row.SalesCount.ToString("N0", Invariant), right: true);
            Body(table, fonts, Number(row.TotalAmount), right: true, bold: true);
            Body(table, fonts, Number(row.AverageSale), right: true);
            Body(table, fonts, $"{row.ShareOfValuePercent:0.#}%", right: true);
            Body(table, fonts, row.WithoutReferenceCount == 0 ? "—" : row.WithoutReferenceCount.ToString(Invariant), right: true);
        }

        document.Add(table);
    }

    private static void Shops(Document document, Fonts fonts, DesktopSalesReviewCurrency section)
    {
        if (section.ByShop.Count == 0)
        {
            return;
        }

        Heading(document, fonts, "Shops and depots", "Compare on takings per trading day: shops that started mid-period traded fewer days.");
        var table = Grid([22f, 10f, 6f, 7f, 12f, 8f, 11f, 9f, 7f, 8f]);
        Head(table, fonts, "Shop", "<Channel", "Days", "Sales", "Takings", "Share", "Per day", "Avg sale", "Card", "Margin");

        foreach (var shop in section.ByShop)
        {
            var name = new Cell()
                .Add(Text(Clean(shop.Label), fonts.Bold, 8))
                .Add(Text(shop.WarehouseCode + (shop.StartedInPeriod && shop.FirstSaleDate is { } first ? $" · from {D(first, "d MMM")}" : string.Empty), fonts.Regular, 7).SetFontColor(Muted));
            table.AddCell(Style(name));
            Body(table, fonts, shop.Channel);
            Body(table, fonts, shop.DaysTraded.ToString(Invariant), right: true);
            Body(table, fonts, shop.SalesCount.ToString("N0", Invariant), right: true);
            Body(table, fonts, Number(shop.TotalAmount), right: true, bold: true);
            Body(table, fonts, $"{shop.ShareOfValuePercent:0.#}%", right: true);
            Body(table, fonts, Number(shop.PerTradingDay), right: true);
            Body(table, fonts, Number(shop.AverageSale), right: true);
            Body(table, fonts, $"{shop.NonCashPercent:0.#}%", right: true);
            Body(table, fonts, shop.MarginPercent is { } margin ? $"{margin:0.#}%" : "—", right: true);
        }

        document.Add(table);
    }

    private static void Items(Document document, Fonts fonts, DesktopSalesReviewCurrency section)
    {
        if (section.TopItems.Count == 0)
        {
            return;
        }

        var topShare = section.TopItems[^1].CumulativeSharePercent;
        Heading(document, fonts, "Best sellers",
            $"The top {section.TopItems.Count} of {section.ItemCount:N0} items make {topShare:0.#}% of line value before VAT; the other {Math.Max(0, section.ItemCount - section.TopItems.Count):N0} share {Number(section.TailNetAmount)}.");

        var table = Grid([5f, 33f, 9f, 7f, 8f, 9f, 12f, 7f, 5f, 5f]);
        Head(table, fonts, "#", "<Item", "Qty", "Sales", "Per sale", "Unit price", "Before VAT", "Share", "Cum.", "<ABC");

        var rank = 0;
        foreach (var item in section.TopItems)
        {
            Body(table, fonts, (++rank).ToString(Invariant), right: true);
            var name = new Cell()
                .Add(Text(item.ItemCode, fonts.Bold, 8))
                .Add(Text(Clean(item.ItemDescription ?? string.Empty), fonts.Regular, 7).SetFontColor(Muted));
            table.AddCell(Style(name));
            Body(table, fonts, item.Quantity.ToString("N0", Invariant), right: true);
            Body(table, fonts, item.SalesCount.ToString("N0", Invariant), right: true);
            Body(table, fonts, item.UnitsPerSale.ToString("0.#", Invariant), right: true);
            Body(table, fonts, item.AverageUnitPrice.ToString("N2", Invariant), right: true);
            Body(table, fonts, Number(item.NetAmount), right: true, bold: true);
            Body(table, fonts, $"{item.ShareOfNetPercent:0.#}%", right: true);
            Body(table, fonts, $"{item.CumulativeSharePercent:0}%", right: true);
            Body(table, fonts, item.AbcClass);
        }

        document.Add(table);
    }

    private static void Spreads(Document document, Fonts fonts, DesktopSalesReviewCurrency section)
    {
        if (section.PriceSpreads.Count == 0)
        {
            return;
        }

        var labels = section.ByShop.ToDictionary(s => s.WarehouseCode, s => s.Label, StringComparer.OrdinalIgnoreCase);
        Heading(document, fonts, "Prices that differ between shops", "Realised price before VAT: value over quantity. Confirm each against the price lists.");
        var table = Grid([30f, 22f, 22f, 10f, 16f]);
        Head(table, fonts, "Item", "<Lowest", "<Highest", "Spread", "At the higher price");

        foreach (var spread in section.PriceSpreads.Take(15))
        {
            var name = new Cell()
                .Add(Text(spread.ItemCode, fonts.Bold, 8))
                .Add(Text(Clean(spread.ItemDescription ?? string.Empty), fonts.Regular, 7).SetFontColor(Muted));
            table.AddCell(Style(name));
            Body(table, fonts, $"{spread.LowUnitPrice:N2} · {Clean(labels.GetValueOrDefault(spread.LowWarehouseCode, spread.LowWarehouseCode))}");
            Body(table, fonts, $"{spread.HighUnitPrice:N2} · {Clean(labels.GetValueOrDefault(spread.HighWarehouseCode, spread.HighWarehouseCode))}");
            Body(table, fonts, $"{spread.SpreadPercent:0.#}%", right: true);
            Body(table, fonts, "+" + Number(spread.UpliftAtHighPrice), right: true, bold: true);
        }

        document.Add(table);
    }

    private static void LargeSales(Document document, Fonts fonts, DesktopSalesReviewCurrency section)
    {
        if (section.LargeSales.Count == 0)
        {
            return;
        }

        var labels = section.ByShop.ToDictionary(s => s.WarehouseCode, s => s.Label, StringComparer.OrdinalIgnoreCase);
        Heading(document, fonts, "Trade-size sales at a counter",
            $"Sales worth {GetDesktopSalesReviewHandler.LargeSaleFactor:0} or more times their shop's average.");
        var table = Grid([12f, 30f, 14f, 16f, 10f, 10f, 8f]);
        Head(table, fonts, "Day", "<Shop", "<Paid by", "<Customer", "Units", "Total", "× avg");

        foreach (var sale in section.LargeSales)
        {
            Body(table, fonts, D(sale.DocDate, "ddd d MMM"));
            Body(table, fonts, Clean(labels.GetValueOrDefault(sale.WarehouseCode, sale.WarehouseCode)));
            Body(table, fonts, sale.PaymentMethod);
            Body(table, fonts, sale.CustomerCode ?? "walk-in");
            Body(table, fonts, sale.Quantity.ToString("N0", Invariant), right: true);
            Body(table, fonts, Number(sale.TotalAmount), right: true, bold: true);
            Body(table, fonts, sale.TimesShopAverage.ToString("0.#", Invariant), right: true);
        }

        document.Add(table);
    }

    private static void Health(Document document, Fonts fonts, DesktopSalesReviewBusiness business)
    {
        var health = business.Health;
        Heading(document, fonts, $"{business.Label}: fiscalisation and SAP posting", $"{health.SalesCount:N0} sales in the period, every currency.");

        var table = Grid([40f, 15f, 45f]);
        Head(table, fonts, "State", "Sales", "Value");
        Row("Fiscalised", health.FiscalSucceeded);
        Row("Waiting to fiscalise", health.FiscalPending);
        Row("Fiscalisation failed", health.FiscalFailed);
        Row("Needs fiscal reconciliation", health.NeedsReconciliation);
        Row("Posted to SAP", health.Posted);
        Row("Waiting to post", health.PostingWaiting);
        Row("SAP refused", health.PostingFailing);
        Row("Payment failed", health.PaymentFailed);
        document.Add(table);

        if (health.OldestUnpostedDate is { } oldest)
        {
            document.Add(Text($"Oldest sale not yet in SAP: {D(oldest, "ddd d MMM yyyy")}.", fonts.Regular, 8).SetFontColor(Muted));
        }

        void Row(string label, ManagementHealthBucket bucket)
        {
            Body(table, fonts, label);
            Body(table, fonts, bucket.SalesCount.ToString("N0", Invariant), right: true);
            Body(table, fonts, bucket.Value.Count == 0 ? "—" : string.Join(" · ", bucket.Value.Select(v => Money(v.Currency, v.Amount))), right: true);
        }
    }

    private static void Method(Document document, Fonts fonts, DesktopSalesReview review)
    {
        Heading(document, fonts, "How this review is worked out", null);
        var notes = new[]
        {
            "Figures are the Desktop Sales Analysis and Management Sales pages for the same period and scope, so they agree with both.",
            "Shops, vending and vans are reviewed apart and never added together; neither are currencies. Amounts include VAT unless marked before VAT.",
            "Takings per trading day divide by the days a shop sold anything, so a shop that opened mid-period is compared fairly.",
            "Traffic and ticket split the change on the previous period: more or fewer sales at the old average, and the average moving at the new count.",
            "Short tendered counts sales whose recorded tender was below their total. Sales with no tender recorded count as neither short nor change.",
            "Realised unit prices are value before VAT over quantity; a blend of full-price and discounted lines shows as a lower price.",
        }.Concat(review.Businesses.Select(business => $"{business.Label} margin: {Clean(business.Margin.Detail)}"));

        foreach (var note in notes)
        {
            document.Add(Text("· " + note, fonts.Regular, 8).SetFontColor(Muted).SetMarginBottom(2));
        }
    }

    private static void PageNumbers(Document document, PdfDocument pdf, Fonts fonts, string title, DesktopSalesReview review)
    {
        var pages = pdf.GetNumberOfPages();
        for (var page = 1; page <= pages; page++)
        {
            var size = pdf.GetPage(page).GetPageSize();
            document.ShowTextAligned(
                Text($"{title} · {D(review.FromDate, "d MMM")} to {D(review.ToDate, "d MMM yyyy")} · page {page} of {pages}", fonts.Regular, 7).SetFontColor(Muted),
                size.GetWidth() / 2, 22, page, TextAlignment.CENTER, VerticalAlignment.BOTTOM, 0);
        }
    }

    // ── Pieces ─────────────────────────────────────────────────────────────────────────────────────

    private static void Heading(Document document, Fonts fonts, string text, string? subtitle)
    {
        document.Add(Text(text, fonts.Bold, 12).SetMarginTop(12).SetMarginBottom(subtitle is null ? 4 : 1));
        if (subtitle is not null)
        {
            document.Add(Text(Clean(subtitle), fonts.Regular, 8).SetFontColor(Muted).SetMarginBottom(4));
        }
    }

    private static void Tile(Table tiles, Fonts fonts, string label, string value, string detail) =>
        tiles.AddCell(new Cell()
            .Add(Text(label.ToUpperInvariant(), fonts.Bold, 6.5f).SetFontColor(Muted).SetCharacterSpacing(0.5f))
            .Add(Text(Clean(value), fonts.Bold, 12).SetMarginTop(1))
            .Add(Text(Clean(detail), fonts.Regular, 7).SetFontColor(Muted))
            .SetBackgroundColor(Band)
            .SetBorder(new SolidBorder(ColorConstants.WHITE, 2))
            .SetPadding(6));

    private static Table Grid(float[] widths) =>
        new Table(UnitValue.CreatePercentArray(widths)).UseAllAvailableWidth().SetMarginBottom(6);

    /// <remarks>The first column and any label written with a leading "&lt;" read left; the rest are figures and read right.</remarks>
    private static void Head(Table table, Fonts fonts, params string[] labels)
    {
        for (var index = 0; index < labels.Length; index++)
        {
            var left = index == 0 || labels[index].StartsWith('<');
            var paragraph = Text(labels[index].TrimStart('<').ToUpperInvariant(), fonts.Bold, 6.5f).SetFontColor(Muted).SetCharacterSpacing(0.4f);
            if (!left)
            {
                paragraph.SetTextAlignment(TextAlignment.RIGHT);
            }

            table.AddHeaderCell(new Cell()
                .Add(paragraph)
                .SetBorder(Border.NO_BORDER)
                .SetBorderBottom(new SolidBorder(Accent, 0.8f))
                .SetPadding(3));
        }
    }

    private static void Body(Table table, Fonts fonts, string text, bool right = false, bool bold = false)
    {
        var paragraph = Text(Clean(text), bold ? fonts.Bold : fonts.Regular, 8);
        if (right)
        {
            paragraph.SetTextAlignment(TextAlignment.RIGHT);
        }

        table.AddCell(Style(new Cell().Add(paragraph)));
    }

    private static Cell Style(Cell cell) =>
        cell.SetBorder(Border.NO_BORDER).SetBorderBottom(new SolidBorder(Rule, 0.5f)).SetPadding(3);

    private static Cell BarCell(decimal value, decimal max, Color color)
    {
        var width = max == 0 ? 0f : (float)Math.Max(1m, value / max * 100m);
        return Style(new Cell().Add(new Div()
                .SetWidth(UnitValue.CreatePercentValue(width))
                .SetHeight(6)
                .SetBackgroundColor(color)))
            .SetVerticalAlignment(VerticalAlignment.MIDDLE);
    }

    private static Paragraph Text(string text, PdfFont font, float size) =>
        new Paragraph(text).SetFont(font).SetFontSize(size).SetMargin(0).SetMultipliedLeading(1.2f);

    private static string D(DateTime date, string format) => date.ToString(format, Invariant);

    private static string Money(string currency, decimal amount) => $"{currency} {amount.ToString("N2", Invariant)}";

    private static string Number(decimal amount) => amount.ToString("N2", Invariant);

    private static string Signed(decimal value) => (value >= 0 ? "+" : "-") + Math.Abs(value).ToString("0.#", Invariant);

    private static string SignedMoney(decimal value) => (value >= 0 ? "+" : "-") + Math.Abs(value).ToString("N0", Invariant);

    /// <summary>Maps the characters outside WinAnsi that the review's wording uses.</summary>
    private static string Clean(string text) =>
        text.Replace("→", "to").Replace("−", "-").Replace("‘", "'").Replace("’", "'");

    private sealed record Fonts(PdfFont Regular, PdfFont Bold);
}
