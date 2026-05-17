using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Draw;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;
using ShopInventory.DTOs;

namespace ShopInventory.Services;

public interface ISalesOrderPdfService
{
    Task<byte[]> GenerateSalesOrderPdfAsync(
        SalesOrderDto order,
        string? customerVatNo = null,
        string? customerTinNumber = null,
        string? customerPhone = null,
        string? customerEmail = null);
}

public sealed class SalesOrderPdfService(ILogger<SalesOrderPdfService> logger) : ISalesOrderPdfService
{
    private PdfFont boldFont = null!;
    private PdfFont regularFont = null!;

    private static readonly Color Black = new DeviceRgb(0, 0, 0);
    private static readonly Color White = new DeviceRgb(255, 255, 255);
    private static readonly Color Accent = new DeviceRgb(102, 126, 234);
    private static readonly Border ThinBorder = new SolidBorder(Black, 0.5f);

    public Task<byte[]> GenerateSalesOrderPdfAsync(
        SalesOrderDto order,
        string? customerVatNo = null,
        string? customerTinNumber = null,
        string? customerPhone = null,
        string? customerEmail = null)
    {
        try
        {
            InitializeFonts();

            using var memoryStream = new MemoryStream();
            var writer = new PdfWriter(memoryStream, new WriterProperties());
            writer.SetCloseStream(false);

            var pdf = new PdfDocument(writer);
            var document = new Document(pdf, PageSize.A4);
            document.SetMargins(24, 30, 28, 30);

            AddPageHeader(document, order);
            AddCompanyHeader(document);
            AddDocumentInfo(document, order);
            AddCustomerAndDeliverySection(document, order, customerVatNo, customerTinNumber, customerPhone, customerEmail);
            AddLineItemsTable(document, order);
            AddTotalsSection(document, order);
            AddCommentsSection(document, order);

            document.Close();
            return Task.FromResult(memoryStream.ToArray());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating sales order PDF for {OrderNumber}", order.OrderNumber);
            throw;
        }
    }

    private void InitializeFonts()
    {
        boldFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
        regularFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
    }

    private void AddPageHeader(Document document, SalesOrderDto order)
    {
        var table = new Table(new float[] { 3.5f, 1.5f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(8);

        var titleCell = NoBorderCell();
        titleCell.Add(new Paragraph("Sales Order")
            .SetFont(boldFont)
            .SetFontSize(16)
            .SetFontColor(Black)
            .SetMarginBottom(2));
        titleCell.Add(new Paragraph(order.OrderNumber)
            .SetFont(regularFont)
            .SetFontSize(10)
            .SetFontColor(Accent));
        table.AddCell(titleCell);

        var metaCell = NoBorderCell().SetTextAlignment(TextAlignment.RIGHT);
        metaCell.Add(SmallLine("KEFALOS CHEESE PRODUCTS PVT (LTD)", true));
        metaCell.Add(SmallLine("Generated on " + DateTime.UtcNow.ToString("dd MMM yyyy HH:mm 'UTC'")));
        table.AddCell(metaCell);

        document.Add(table);
        document.Add(new LineSeparator(new SolidLine(1)).SetMarginBottom(10));
    }

    private void AddCompanyHeader(Document document)
    {
        var table = new Table(new float[] { 1.4f, 2.1f, 2.1f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(10);

        var logoCell = NoBorderCell().SetPaddingRight(8).SetVerticalAlignment(VerticalAlignment.MIDDLE);
        try
        {
            var logoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "kefalos-logo.jpg");
            if (File.Exists(logoPath))
            {
                logoCell.Add(new Image(ImageDataFactory.Create(logoPath)).SetWidth(96).SetAutoScale(true));
            }
            else
            {
                logoCell.Add(new Paragraph("KEFALOS")
                    .SetFont(boldFont)
                    .SetFontSize(14)
                    .SetFontColor(Accent));
            }
        }
        catch
        {
            logoCell.Add(new Paragraph("KEFALOS")
                .SetFont(boldFont)
                .SetFontSize(14)
                .SetFontColor(Accent));
        }

        table.AddCell(logoCell);

        table.AddCell(AddressCell(
            "ADMINISTRATION OFFICE",
            "35C Kingsmead Road, Borrowdale",
            "Harare, Zimbabwe",
            "Tel: +263/242 764 301/02/03",
            "Email: marketing@kefaloscheese.com",
            "Website: www.kefalosfood.com"));

        table.AddCell(AddressCell(
            "FACTORY",
            "Bhara Bhara Farm, Mubaira Road",
            "Harare South, Harare, Zimbabwe",
            "Tel: +263 242 613 454/5/6/5",
            "Email: kefalos@kefaloscheese.com",
            "Facebook: www.facebook.com/kefalosproducts"));

        document.Add(table);
    }

    private void AddDocumentInfo(Document document, SalesOrderDto order)
    {
        var table = new Table(new float[] { 1f, 1f, 1f, 1f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(10);

        AddInfoCell(table, "Order Date", FormatDate(order.OrderDate));
        AddInfoCell(table, "Delivery Date", FormatDate(order.DeliveryDate));
        AddInfoCell(table, "Customer Ref", order.CustomerRefNo ?? "-");
        AddInfoCell(table, "Status", order.StatusName);
        AddInfoCell(table, "Currency", order.Currency ?? "-");
        AddInfoCell(table, "Sales Person", order.SalesPersonName ?? "-");
        AddInfoCell(table, "Warehouse", order.WarehouseCode ?? "-");
        AddInfoCell(table, "Source", order.Source.ToString());

        document.Add(table);
    }

    private void AddCustomerAndDeliverySection(
        Document document,
        SalesOrderDto order,
        string? customerVatNo,
        string? customerTinNumber,
        string? customerPhone,
        string? customerEmail)
    {
        var table = new Table(new float[] { 1f, 1f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(12);

        table.AddCell(InfoBox(
            "Customer",
            ("Customer Name", order.CardName ?? order.CardCode),
            ("Customer Code", order.CardCode),
            ("VAT Number", customerVatNo ?? "-"),
            ("TIN Number", customerTinNumber ?? "-"),
            ("Phone", customerPhone ?? "-"),
            ("Email", customerEmail ?? "-"),
            ("Bill To", NormalizeMultiline(order.BillToAddress))));

        table.AddCell(InfoBox(
            "Delivery",
            ("Ship To", NormalizeMultiline(order.ShipToAddress)),
            ("Requested Date", FormatDate(order.DeliveryDate)),
            ("Created By", order.CreatedByUserName ?? "-"),
            ("Approved By", order.ApprovedByUserName ?? "-"),
            ("Approved Date", FormatDate(order.ApprovedDate)),
            ("Synced", order.IsSynced ? "Yes" : "No")));

        document.Add(table);
    }

    private void AddLineItemsTable(Document document, SalesOrderDto order)
    {
        var table = new Table(new float[] { 1.4f, 2.6f, 0.8f, 1f, 0.8f, 0.8f, 1.1f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(12);

        AddHeaderCell(table, "Item Code");
        AddHeaderCell(table, "Description");
        AddHeaderCell(table, "Qty", TextAlignment.RIGHT);
        AddHeaderCell(table, "Unit Price", TextAlignment.RIGHT);
        AddHeaderCell(table, "Disc %", TextAlignment.RIGHT);
        AddHeaderCell(table, "Tax %", TextAlignment.RIGHT);
        AddHeaderCell(table, "Line Total", TextAlignment.RIGHT);

        if (order.Lines.Count == 0)
        {
            var emptyCell = new Cell(1, 7)
                .SetBorder(ThinBorder)
                .SetPadding(8)
                .SetTextAlignment(TextAlignment.CENTER);
            emptyCell.Add(new Paragraph("No line items available")
                .SetFont(regularFont)
                .SetFontSize(9)
                .SetFontColor(Black));
            table.AddCell(emptyCell);
        }
        else
        {
            foreach (var line in order.Lines.OrderBy(l => l.LineNum))
            {
                AddBodyCell(table, line.ItemCode);
                AddBodyCell(table, line.ItemDescription ?? "-");
                AddBodyCell(table, line.Quantity.ToString("N2"), TextAlignment.RIGHT);
                AddBodyCell(table, line.UnitPrice.ToString("N2"), TextAlignment.RIGHT);
                AddBodyCell(table, line.DiscountPercent.ToString("N2"), TextAlignment.RIGHT);
                AddBodyCell(table, line.TaxPercent.ToString("N2"), TextAlignment.RIGHT);
                AddBodyCell(table, line.LineTotal.ToString("N2"), TextAlignment.RIGHT);
            }
        }

        document.Add(table);
    }

    private void AddTotalsSection(Document document, SalesOrderDto order)
    {
        var wrapper = new Table(new float[] { 2.4f, 1f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(10);

        wrapper.AddCell(NoBorderCell());

        var totals = new Table(new float[] { 1.2f, 1f }).SetWidth(UnitValue.CreatePercentValue(100));
        AddTotalRow(totals, "Subtotal", order.SubTotal);
        AddTotalRow(totals, "Discount", -order.DiscountAmount);
        AddTotalRow(totals, "VAT", order.TaxAmount);
        AddTotalRow(totals, "Total", order.DocTotal, true);

        var totalsCell = NoBorderCell();
        totalsCell.Add(totals);
        wrapper.AddCell(totalsCell);

        document.Add(wrapper);
    }

    private void AddCommentsSection(Document document, SalesOrderDto order)
    {
        if (string.IsNullOrWhiteSpace(order.Comments))
        {
            return;
        }

        var section = new Div()
            .SetBorder(ThinBorder)
            .SetPadding(8)
            .SetMarginTop(6);

        section.Add(new Paragraph("Comments")
            .SetFont(boldFont)
            .SetFontSize(9)
            .SetFontColor(Black)
            .SetMarginBottom(4));
        section.Add(new Paragraph(order.Comments)
            .SetFont(regularFont)
            .SetFontSize(9)
            .SetMargin(0));

        document.Add(section);
    }

    private Cell AddressCell(string title, params string[] lines)
    {
        var cell = NoBorderCell().SetVerticalAlignment(VerticalAlignment.TOP).SetPaddingLeft(6);
        cell.Add(new Paragraph(title)
            .SetFont(boldFont)
            .SetFontSize(7.5f)
            .SetMarginBottom(2));

        foreach (var line in lines)
        {
            cell.Add(SmallLine(line));
        }

        return cell;
    }

    private Cell InfoBox(string title, params (string Label, string Value)[] rows)
    {
        var cell = new Cell()
            .SetBorder(ThinBorder)
            .SetPadding(0)
            .SetMarginRight(4);

        var header = new Cell()
            .SetBorder(Border.NO_BORDER)
            .SetBackgroundColor(new DeviceRgb(248, 250, 252))
            .SetPadding(6);
        header.Add(new Paragraph(title)
            .SetFont(boldFont)
            .SetFontSize(9)
            .SetMargin(0));

        var inner = new Table(1).SetWidth(UnitValue.CreatePercentValue(100));
        inner.AddCell(header);

        var body = new Cell().SetBorder(Border.NO_BORDER).SetPadding(6);
        foreach (var (label, value) in rows)
        {
            body.Add(new Paragraph()
                .SetMargin(0)
                .Add(new Text(label + ": ").SetFont(boldFont).SetFontSize(8))
                .Add(new Text(value).SetFont(regularFont).SetFontSize(8)));
        }

        inner.AddCell(body);
        cell.Add(inner);
        return cell;
    }

    private void AddInfoCell(Table table, string label, string value)
    {
        var cell = new Cell()
            .SetBorder(ThinBorder)
            .SetPadding(6);
        cell.Add(new Paragraph(label)
            .SetFont(boldFont)
            .SetFontSize(7.5f)
            .SetFontColor(Black)
            .SetMarginBottom(2));
        cell.Add(new Paragraph(value)
            .SetFont(regularFont)
            .SetFontSize(8.5f)
            .SetFontColor(Black)
            .SetMargin(0));
        table.AddCell(cell);
    }

    private void AddHeaderCell(Table table, string text, TextAlignment alignment = TextAlignment.LEFT)
    {
        var cell = new Cell()
            .SetBackgroundColor(new DeviceRgb(248, 250, 252))
            .SetBorder(ThinBorder)
            .SetPadding(6)
            .SetTextAlignment(alignment);
        cell.Add(new Paragraph(text)
            .SetFont(boldFont)
            .SetFontSize(8)
            .SetMargin(0));
        table.AddCell(cell);
    }

    private void AddBodyCell(Table table, string text, TextAlignment alignment = TextAlignment.LEFT)
    {
        var cell = new Cell()
            .SetBorder(ThinBorder)
            .SetPadding(6)
            .SetTextAlignment(alignment);
        cell.Add(new Paragraph(text)
            .SetFont(regularFont)
            .SetFontSize(8)
            .SetMargin(0));
        table.AddCell(cell);
    }

    private void AddTotalRow(Table table, string label, decimal value, bool emphasize = false)
    {
        table.AddCell(new Cell()
            .SetBorder(ThinBorder)
            .SetPadding(6)
            .Add(new Paragraph(label)
                .SetFont(emphasize ? boldFont : regularFont)
                .SetFontSize(emphasize ? 9 : 8)
                .SetMargin(0)));

        table.AddCell(new Cell()
            .SetBorder(ThinBorder)
            .SetPadding(6)
            .SetTextAlignment(TextAlignment.RIGHT)
            .Add(new Paragraph(value.ToString("N2"))
                .SetFont(emphasize ? boldFont : regularFont)
                .SetFontSize(emphasize ? 9 : 8)
                .SetFontColor(emphasize ? Accent : Black)
                .SetMargin(0)));
    }

    private Paragraph SmallLine(string text, bool bold = false)
    {
        return new Paragraph(text)
            .SetFont(bold ? boldFont : regularFont)
            .SetFontSize(7)
            .SetFontColor(Black)
            .SetMargin(0)
            .SetMultipliedLeading(1);
    }

    private Cell NoBorderCell()
    {
        return new Cell().SetBorder(Border.NO_BORDER).SetPadding(0);
    }

    private static string FormatDate(DateTime? value)
    {
        return value.HasValue ? value.Value.ToString("dd MMM yyyy") : "-";
    }

    private static string NormalizeMultiline(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("\r", string.Empty).Replace("\n", ", ");
    }
}