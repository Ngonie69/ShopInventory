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
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakagesPdf;

/// <summary>
/// Lays a view of the market breakage list out as a landscape A4 document: the figures, then by van and
/// by product, then every report with its lines, reported beside counted.
/// </summary>
/// <remarks>
/// Pure layout: every number comes from the export. The rep's figures are printed as reported and the
/// office's count beside them, as the page shows them. The standard Helvetica faces cover WinAnsi only,
/// so the few typographic characters outside it are mapped to plain equivalents.
/// </remarks>
internal static class MarketBreakagesPdfRenderer
{
    private static readonly Color Ink = new DeviceRgb(15, 26, 34);
    private static readonly Color Muted = new DeviceRgb(96, 110, 120);
    private static readonly Color Rule = new DeviceRgb(221, 228, 232);
    private static readonly Color Accent = new DeviceRgb(29, 95, 138);
    private static readonly Color Band = new DeviceRgb(246, 248, 250);
    private static readonly Color Good = new DeviceRgb(30, 125, 60);
    private static readonly Color Bad = new DeviceRgb(192, 49, 49);
    private static readonly Color Warn = new DeviceRgb(178, 111, 0);

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private const int ReportColumns = 10;
    private const int ReportOwnColumns = 5;

    public static byte[] Render(MarketBreakageExportDto export, DateTime generatedAtCat)
    {
        using var stream = new MemoryStream();
        var writer = new PdfWriter(stream);
        writer.SetCloseStream(false);
        var pdf = new PdfDocument(writer);
        pdf.GetDocumentInfo().SetTitle($"Market breakages - {ScopeLabel(export.Status)}");

        var fonts = new Fonts(
            PdfFontFactory.CreateFont(StandardFonts.HELVETICA),
            PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD));

        var document = new Document(pdf, PageSize.A4.Rotate(), false);
        document.SetMargins(32, 32, 40, 32);
        document.SetFont(fonts.Regular).SetFontSize(9).SetFontColor(Ink);

        Header(document, fonts, export, generatedAtCat);

        if (export.Reports.Count == 0)
        {
            document.Add(Text("No breakage reports match this view.", fonts.Regular, 10).SetMarginTop(12));
        }
        else
        {
            Figures(document, fonts, export.Totals);
            Groups(document, fonts, "By van", "Most units reported first.", "Van", "<Reps", export.ByVan);
            Groups(document, fonts, "By product", "Most units reported first.", "Item", "<Description", export.ByProduct);
            Reports(document, fonts, export.Reports);
        }

        PageNumbers(document, pdf, fonts, export, generatedAtCat);

        document.Close();
        return stream.ToArray();
    }

    /// <summary>What the status filter is called on paper.</summary>
    public static string ScopeLabel(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "All reports";
        }

        if (string.Equals(status, MarketBreakageFilters.Open, StringComparison.OrdinalIgnoreCase))
        {
            return "Waiting on the office";
        }

        var exact = MarketBreakageStatuses.All.FirstOrDefault(value =>
            string.Equals(value, status, StringComparison.OrdinalIgnoreCase));
        return StatusLabel(exact ?? status);
    }

    // ── Sections ───────────────────────────────────────────────────────────────────────────────────

    private static void Header(Document document, Fonts fonts, MarketBreakageExportDto export, DateTime generatedAtCat)
    {
        document.Add(Text("VAN SALES · MARKET BREAKAGES", fonts.Bold, 8).SetFontColor(Accent).SetCharacterSpacing(0.8f));
        document.Add(Text($"Market breakages: {ScopeLabel(export.Status)}", fonts.Bold, 20).SetMarginTop(2));

        var scope = $"{export.TotalCount:N0} {(export.TotalCount == 1 ? "report" : "reports")}";
        if (export.Search is { } search)
        {
            scope += $" matching \"{Clean(search)}\"";
        }

        document.Add(Text(scope + " · stock broken or damaged in transit on the vans", fonts.Regular, 10).SetFontColor(Muted));
        document.Add(Text($"Generated {D(generatedAtCat, "d MMM yyyy, HH:mm")} CAT · all times CAT", fonts.Regular, 8)
            .SetFontColor(Muted)
            .SetMarginBottom(export.Truncated ? 2 : 10));

        if (export.Truncated)
        {
            document.Add(Text(
                    $"Only the newest {export.Reports.Count:N0} of {export.TotalCount:N0} reports are included. Narrow the view to see the rest.",
                    fonts.Bold, 8)
                .SetFontColor(Bad)
                .SetMarginBottom(10));
        }
    }

    private static void Figures(Document document, Fonts fonts, MarketBreakageExportTotalsDto totals)
    {
        var tiles = new Table(UnitValue.CreatePercentArray([20f, 20f, 20f, 20f, 20f])).UseAllAvailableWidth().SetMarginBottom(6);
        Tile(tiles, fonts, "Reports", totals.Reports.ToString("N0", Invariant),
            totals.OpenReports == 0 ? "none waiting on the office" : $"{totals.OpenReports:N0} waiting on the office");
        Tile(tiles, fonts, "Units reported", Quantity(totals.ReportedQuantity), $"{totals.Lines:N0} product lines");
        Tile(tiles, fonts, "Units counted", Quantity(totals.CountedQuantity), "what the office confirmed");
        Tile(tiles, fonts, "Moved to returns", Quantity(totals.TransferredQuantity), "transferred in SAP");
        Tile(tiles, fonts, "Rejected", Quantity(totals.RejectedQuantity), "units reported, sent back to the rep");
        document.Add(tiles);
    }

    private static void Groups(
        Document document, Fonts fonts, string heading, string subtitle, string codeLabel, string nameLabel,
        List<MarketBreakageExportGroupDto> groups)
    {
        if (groups.Count == 0)
        {
            return;
        }

        Heading(document, fonts, heading, subtitle);
        var table = Grid([12f, 40f, 9f, 13f, 13f, 13f]);
        Head(table, fonts, codeLabel, nameLabel, "Reports", "Reported", "Counted", "Moved to returns");

        foreach (var group in groups)
        {
            Body(table, fonts, group.Code, bold: true);
            Body(table, fonts, group.Name ?? string.Empty);
            Body(table, fonts, group.Reports.ToString("N0", Invariant), right: true);
            Body(table, fonts, Quantity(group.ReportedQuantity), right: true);
            Body(table, fonts, Quantity(group.CountedQuantity), right: true);
            Body(table, fonts, Quantity(group.TransferredQuantity), right: true, bold: group.TransferredQuantity != 0);
        }

        document.Add(table);
    }

    /// <summary>
    /// Every report, one row per product line. The report's own cells are written on its first line
    /// only, and a heavier rule opens each report, so a report reads as one block.
    /// </summary>
    private static void Reports(Document document, Fonts fonts, List<MarketBreakageDetailDto> reports)
    {
        Heading(document, fonts, "Reports", "Newest first. Counted is the office's confirmed count; the difference is counted less reported.");
        var table = Grid([8f, 8f, 13f, 15f, 11f, 21f, 8f, 5.5f, 5.5f, 5f]);
        Head(table, fonts, "Report", "<Van", "<Rep", "<Shop", "<Status", "<Product", "<Reason", "Reported", "Counted", "Diff");

        foreach (var report in reports)
        {
            if (report.Lines.Count == 0)
            {
                ReportCells(table, fonts, report);
                table.AddCell(Open(new Cell(1, ReportColumns - ReportOwnColumns).Add(Text("No lines", fonts.Regular, 8).SetFontColor(Muted))));
            }

            for (var index = 0; index < report.Lines.Count; index++)
            {
                var first = index == 0;
                var line = report.Lines[index];

                if (first)
                {
                    ReportCells(table, fonts, report);
                }
                else
                {
                    for (var blank = 0; blank < ReportOwnColumns; blank++)
                    {
                        table.AddCell(Style(new Cell()));
                    }
                }

                var product = new Cell().Add(Text(Clean(string.IsNullOrWhiteSpace(line.ItemDescription) ? line.ItemCode : line.ItemDescription), fonts.Regular, 8));
                if (!string.IsNullOrWhiteSpace(line.ItemDescription))
                {
                    product.Add(Text(line.ItemCode, fonts.Regular, 7).SetFontColor(Muted));
                }

                table.AddCell(Mark(first, product));
                table.AddCell(Mark(first, Plain(fonts, line.Reason ?? "-", muted: line.Reason is null)));
                table.AddCell(Mark(first, Plain(fonts, Quantity(line.ReportedQuantity), right: true)));
                table.AddCell(Mark(first, Plain(fonts, line.ConfirmedQuantity is { } counted ? Quantity(counted) : "-", right: true, bold: line.ConfirmedQuantity is not null)));

                var diff = line.ConfirmedQuantity is { } confirmed ? confirmed - line.ReportedQuantity : (decimal?)null;
                var diffCell = Plain(fonts, diff is { } value ? Signed(value) : "-", right: true, bold: diff is not null and not 0);
                if (diff < 0)
                {
                    diffCell.SetFontColor(Bad);
                }
                else if (diff > 0)
                {
                    diffCell.SetFontColor(Warn);
                }

                table.AddCell(Mark(first, diffCell));
            }

            Notes(table, fonts, report);
        }

        document.Add(table);
    }

    /// <summary>The report's own cells, on its first line.</summary>
    private static void ReportCells(Table table, Fonts fonts, MarketBreakageDetailDto report)
    {
        table.AddCell(Open(new Cell()
            .Add(Text($"#{report.Id}", fonts.Bold, 8))
            .Add(Text(D(AuditService.ToCAT(report.CapturedAtUtc), "d MMM yyyy HH:mm"), fonts.Regular, 7).SetFontColor(Muted))));
        table.AddCell(Open(Plain(fonts, report.VanWarehouseCode, bold: true)));
        table.AddCell(Open(Plain(fonts, report.ReportedByName)));
        table.AddCell(Open(Shop(fonts, report)));
        table.AddCell(Open(Status(fonts, report)));
    }

    /// <summary>What the rep wrote, why it was decided as it was, and why a transfer failed, under the report.</summary>
    private static void Notes(Table table, Fonts fonts, MarketBreakageDetailDto report)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(report.Remarks))
        {
            notes.Add($"{report.ReportedByName} wrote: {report.Remarks}");
        }

        if (!string.IsNullOrWhiteSpace(report.DecisionRemarks))
        {
            notes.Add($"{report.DecidedByName ?? "The office"}: {report.DecisionRemarks}");
        }

        if (!string.IsNullOrWhiteSpace(report.LastError) && report.Status != MarketBreakageStatuses.Transferred)
        {
            notes.Add($"Transfer failed: {report.LastError}");
        }

        if (notes.Count == 0)
        {
            return;
        }

        table.AddCell(Style(new Cell()));
        table.AddCell(Style(new Cell(1, ReportColumns - 1)
            .Add(Text(Clean(string.Join("   ·   ", notes)), fonts.Regular, 7.5f).SetFontColor(Muted))));
    }

    private static Cell Shop(Fonts fonts, MarketBreakageDetailDto report)
    {
        if (report.CardName is null && report.CardCode is null)
        {
            return Plain(fonts, "In transit", muted: true);
        }

        var cell = new Cell().Add(Text(Clean(report.CardName ?? report.CardCode!), fonts.Regular, 8));
        if (report.CardName is not null && !string.IsNullOrWhiteSpace(report.CardCode))
        {
            cell.Add(Text(report.CardCode, fonts.Regular, 7).SetFontColor(Muted));
        }

        return cell;
    }

    private static Cell Status(Fonts fonts, MarketBreakageDetailDto report)
    {
        var color = report.Status switch
        {
            MarketBreakageStatuses.Transferred => Good,
            MarketBreakageStatuses.TransferFailed => Bad,
            MarketBreakageStatuses.Rejected => Muted,
            _ => Accent
        };

        var cell = new Cell().Add(Text(StatusLabel(report.Status), fonts.Bold, 8).SetFontColor(color));
        var detail = report.Status switch
        {
            MarketBreakageStatuses.Transferred => (report.SapDocNum is { } docNum ? $"SAP transfer {docNum}" : "SAP transfer")
                + (report.TransferredAtUtc is { } at ? $", {D(AuditService.ToCAT(at), "d MMM")}" : string.Empty),
            MarketBreakageStatuses.Rejected => (report.DecidedByName ?? "the office")
                + (report.DecidedAtUtc is { } at ? $", {D(AuditService.ToCAT(at), "d MMM")}" : string.Empty),
            _ => null
        };

        if (detail is not null)
        {
            cell.Add(Text(Clean(detail), fonts.Regular, 7).SetFontColor(Muted));
        }

        return cell;
    }

    private static void PageNumbers(Document document, PdfDocument pdf, Fonts fonts, MarketBreakageExportDto export, DateTime generatedAtCat)
    {
        var pages = pdf.GetNumberOfPages();
        for (var page = 1; page <= pages; page++)
        {
            var size = pdf.GetPage(page).GetPageSize();
            document.ShowTextAligned(
                Text($"Market breakages · {ScopeLabel(export.Status)} · {D(generatedAtCat, "d MMM yyyy HH:mm")} · page {page} of {pages}", fonts.Regular, 7).SetFontColor(Muted),
                size.GetWidth() / 2, 20, page, TextAlignment.CENTER, VerticalAlignment.BOTTOM, 0);
        }
    }

    // ── Pieces ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The page's word for a status: a pending report is one the office has to count.</summary>
    private static string StatusLabel(string status) => status switch
    {
        MarketBreakageStatuses.Pending => "To count",
        MarketBreakageStatuses.TransferFailed => "Transfer failed",
        _ => status
    };

    private static void Heading(Document document, Fonts fonts, string text, string? subtitle)
    {
        document.Add(Text(text, fonts.Bold, 12).SetMarginTop(10).SetMarginBottom(subtitle is null ? 4 : 1));
        if (subtitle is not null)
        {
            document.Add(Text(Clean(subtitle), fonts.Regular, 8).SetFontColor(Muted).SetMarginBottom(4));
        }
    }

    private static void Tile(Table tiles, Fonts fonts, string label, string value, string detail) =>
        tiles.AddCell(new Cell()
            .Add(Text(label.ToUpperInvariant(), fonts.Bold, 6.5f).SetFontColor(Muted).SetCharacterSpacing(0.5f))
            .Add(Text(Clean(value), fonts.Bold, 14).SetMarginTop(1))
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

    private static void Body(Table table, Fonts fonts, string text, bool right = false, bool bold = false) =>
        table.AddCell(Style(Plain(fonts, text, right, bold)));

    private static Cell Plain(Fonts fonts, string text, bool right = false, bool bold = false, bool muted = false)
    {
        var paragraph = Text(Clean(text), bold ? fonts.Bold : fonts.Regular, 8);
        if (right)
        {
            paragraph.SetTextAlignment(TextAlignment.RIGHT);
        }

        if (muted)
        {
            paragraph.SetFontColor(Muted);
        }

        return new Cell().Add(paragraph);
    }

    private static Cell Style(Cell cell) =>
        cell.SetBorder(Border.NO_BORDER).SetBorderBottom(new SolidBorder(Rule, 0.5f)).SetPadding(3);

    /// <summary>A cell on a report's first line: a heavier rule above it opens the report.</summary>
    private static Cell Open(Cell cell) => Style(cell).SetBorderTop(new SolidBorder(Accent, 0.6f));

    private static Cell Mark(bool first, Cell cell) => first ? Open(cell) : Style(cell);

    private static Paragraph Text(string text, PdfFont font, float size) =>
        new Paragraph(text).SetFont(font).SetFontSize(size).SetMargin(0).SetMultipliedLeading(1.2f);

    private static string D(DateTime date, string format) => date.ToString(format, Invariant);

    private static string Quantity(decimal value) => value.ToString("#,##0.##", Invariant);

    private static string Signed(decimal value) => value switch
    {
        0 => "0",
        > 0 => "+" + Quantity(value),
        _ => "-" + Quantity(-value)
    };

    /// <summary>Maps the characters outside WinAnsi that names and remarks are likely to carry.</summary>
    private static string Clean(string text) =>
        text.Replace("→", "to").Replace("−", "-").Replace("‘", "'").Replace("’", "'").Replace("“", "\"").Replace("”", "\"");

    private sealed record Fonts(PdfFont Regular, PdfFont Bold);
}
