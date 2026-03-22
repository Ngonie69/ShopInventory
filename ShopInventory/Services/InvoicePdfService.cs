using iText.Kernel.Pdf;
using iText.Kernel.Font;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;
using ShopInventory.DTOs;

namespace ShopInventory.Services;

public interface IInvoicePdfService
{
    Task<byte[]> GenerateInvoicePdfAsync(InvoiceDto invoice);
}

public class InvoicePdfService : IInvoicePdfService
{
    private readonly ILogger<InvoicePdfService> _logger;
    private PdfFont _boldFont = null!;
    private PdfFont _regularFont = null!;

    private static readonly Color Black = new DeviceRgb(0, 0, 0);
    private static readonly Color White = new DeviceRgb(255, 255, 255);
    private static readonly Color BorderColor = new DeviceRgb(0, 0, 0);
    private static readonly Color RedText = new DeviceRgb(200, 0, 0);

    // Thin border used throughout
    private static Border ThinBorder => new SolidBorder(Black, 0.5f);

    public InvoicePdfService(ILogger<InvoicePdfService> logger)
    {
        _logger = logger;
    }

    private void InitializeFonts()
    {
        _boldFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
        _regularFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
    }

    public async Task<byte[]> GenerateInvoicePdfAsync(InvoiceDto invoice)
    {
        try
        {
            InitializeFonts();

            using var memoryStream = new MemoryStream();
            var writerProperties = new WriterProperties();
            var writer = new PdfWriter(memoryStream, writerProperties);
            writer.SetCloseStream(false);
            var pdf = new PdfDocument(writer);
            var document = new Document(pdf, PageSize.A4);

            // Margins matching the template
            document.SetMargins(20, 30, 25, 30);

            // 1. "Fiscal Tax Invoice" + "PAGE 1 OF 1"
            AddPageHeader(document);

            // 2. Separator line
            AddHorizontalRule(document);

            // 3. Company header: Logo | Admin Office | Factory
            AddCompanyHeader(document);

            // 4. VAT/TIN (left) + DOC NUMBER/REF/DATE table (right)
            AddVatAndDocInfo(document, invoice);

            // 5. Invoice Address + Delivery Address boxes
            AddAddressBoxes(document, invoice);

            // 6. Line items table
            AddLineItemsTable(document, invoice);

            // 7. Push remaining content to the bottom of the page
            var currentArea = document.GetRenderer().GetCurrentArea();
            if (currentArea != null)
            {
                var bbox = currentArea.GetBBox();
                float availableHeight = bbox.GetHeight();
                float bottomContentHeight = 140f; // estimated height for bank+totals+customer copy
                float spacerHeight = availableHeight - bottomContentHeight;
                if (spacerHeight > 0)
                {
                    document.Add(new Div().SetHeight(spacerHeight));
                }
            }

            // 8. Bank details (left) + Totals table (right)
            AddBankAndTotals(document, invoice);

            // 9. "CUSTOMER COPY" label
            AddCustomerCopyLabel(document);

            document.Close();

            return await Task.FromResult(memoryStream.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating invoice PDF for DocEntry {DocEntry}: {Message}",
                invoice.DocEntry, ex.Message);
            throw;
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 1. PAGE HEADER
    // ────────────────────────────────────────────────────────────────
    private void AddPageHeader(Document document)
    {
        var table = new Table(new float[] { 4f, 1.2f, 0.5f, 0.5f, 0.5f, 0.5f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(3);

        // "Fiscal Tax Invoice" – centered-left
        var titleCell = NoBorderCell();
        titleCell.Add(new Paragraph("Fiscal Tax Invoice")
            .SetFont(_boldFont)
            .SetFontSize(14)
            .SetFontColor(Black)
            .SetTextAlignment(TextAlignment.CENTER));
        table.AddCell(titleCell);

        // "PAGE"
        table.AddCell(NoBorderCell().Add(Para("PAGE", _boldFont, 8, TextAlignment.RIGHT)));
        // "1"
        table.AddCell(NoBorderCell().Add(Para("1", _boldFont, 8, TextAlignment.CENTER)));
        // "OF"
        table.AddCell(NoBorderCell().Add(Para("OF", _regularFont, 8, TextAlignment.CENTER)));
        // "1"
        table.AddCell(NoBorderCell().Add(Para("1", _boldFont, 8, TextAlignment.CENTER)));
        // spacer
        table.AddCell(NoBorderCell());

        document.Add(table);
    }

    // ────────────────────────────────────────────────────────────────
    // 2. HORIZONTAL RULE
    // ────────────────────────────────────────────────────────────────
    private void AddHorizontalRule(Document document)
    {
        var t = new Table(1).SetWidth(UnitValue.CreatePercentValue(100)).SetMarginBottom(6);
        var c = new Cell().SetHeight(1).SetBackgroundColor(Black).SetBorder(Border.NO_BORDER).SetPadding(0);
        t.AddCell(c);
        document.Add(t);
    }

    // ────────────────────────────────────────────────────────────────
    // 3. COMPANY HEADER (Logo | Admin | Factory)
    // ────────────────────────────────────────────────────────────────
    private void AddCompanyHeader(Document document)
    {
        // Row 1: Company name spanning across, above the two-column detail
        document.Add(new Paragraph("KEFALOS CHEESE PRODUCTS PVT (LTD)")
            .SetFont(_boldFont)
            .SetFontSize(10)
            .SetFontColor(Black)
            .SetTextAlignment(TextAlignment.CENTER)
            .SetMarginBottom(4));

        // Row 2: Logo | Admin Office | Factory
        var table = new Table(new float[] { 1.5f, 2.2f, 2.3f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(6);

        // --- Logo ---
        var logoCell = NoBorderCell()
            .SetVerticalAlignment(VerticalAlignment.MIDDLE)
            .SetPaddingRight(5);
        try
        {
            var logoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "kefalos-logo.jpg");
            if (System.IO.File.Exists(logoPath))
            {
                var img = new Image(ImageDataFactory.Create(logoPath))
                    .SetWidth(105)
                    .SetAutoScale(true);
                logoCell.Add(img);
            }
            else
            {
                AddFallbackLogo(logoCell);
            }
        }
        catch
        {
            AddFallbackLogo(logoCell);
        }
        table.AddCell(logoCell);

        // --- Administration Office ---
        var adminCell = NoBorderCell().SetVerticalAlignment(VerticalAlignment.TOP);
        adminCell.Add(new Paragraph("ADMINISTRATION OFFICE")
            .SetFont(_boldFont).SetFontSize(7).SetFontColor(Black).SetMarginBottom(2));
        adminCell.Add(SmallLine("35C Kingsmead Road, Borrowdale"));
        adminCell.Add(SmallLine("Harare, Zimbabwe"));
        adminCell.Add(SmallLine("Tel: +263/242 764 301/02/03"));
        adminCell.Add(SmallLine("Email : marketing@kefaloscheese.com"));
        adminCell.Add(SmallLine("Website: www.kefalosfood.com"));
        table.AddCell(adminCell);

        // --- Factory ---
        var factoryCell = NoBorderCell().SetVerticalAlignment(VerticalAlignment.TOP).SetPaddingLeft(8);
        factoryCell.Add(new Paragraph("FACTORY")
            .SetFont(_boldFont).SetFontSize(7).SetFontColor(Black).SetMarginBottom(2));
        factoryCell.Add(SmallLine("Bhara Bhara Farm, Mubaira Road"));
        factoryCell.Add(SmallLine("Harare South, Harare, Zimbabwe"));
        factoryCell.Add(SmallLine("Tel : +263 242 613 454/5/6/5"));
        factoryCell.Add(SmallLine("Email : kefalos@kefaloscheese.com"));
        factoryCell.Add(SmallLine("Facebook: www.facebook.com/kefalosproducts"));
        table.AddCell(factoryCell);

        document.Add(table);
    }

    // ────────────────────────────────────────────────────────────────
    // 4. VAT/TIN + DOC INFO TABLE
    // ────────────────────────────────────────────────────────────────
    private void AddVatAndDocInfo(Document document, InvoiceDto invoice)
    {
        var outer = new Table(new float[] { 1.1f, 1f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(6);

        // Left: VAT Reg / TIN No (centered in the cell)
        var vatCell = NoBorderCell().SetVerticalAlignment(VerticalAlignment.MIDDLE);
        vatCell.Add(new Paragraph("VAT Reg: 220140892")
            .SetFont(_boldFont).SetFontSize(8).SetTextAlignment(TextAlignment.CENTER).SetMarginBottom(1));
        vatCell.Add(new Paragraph("TIN No: 2000022395")
            .SetFont(_boldFont).SetFontSize(8).SetTextAlignment(TextAlignment.CENTER));
        outer.AddCell(vatCell);

        // Right: Doc detail table
        var docCell = NoBorderCell();
        var dt = new Table(new float[] { 2f, 2.5f }).SetWidth(UnitValue.CreatePercentValue(100));

        AddDocRow(dt, "DOC NUMBER", invoice.DocNum.ToString());
        AddDocRow(dt, "REF :", invoice.CardName ?? "-");

        var dateStr = FormatDate(invoice.DocDate);
        AddDocRow(dt, "DATE", dateStr);

        docCell.Add(dt);
        outer.AddCell(docCell);

        document.Add(outer);
    }

    private void AddDocRow(Table table, string label, string value)
    {
        // Label cell – white bg, black text
        var lc = new Cell().SetBackgroundColor(White)
            .SetPadding(3).SetPaddingLeft(6)
            .SetBorder(new SolidBorder(Black, 0.5f));
        lc.Add(new Paragraph(label).SetFont(_boldFont).SetFontSize(8).SetFontColor(Black));
        table.AddCell(lc);

        // Value cell – white bg
        var vc = new Cell().SetPadding(3).SetPaddingLeft(6)
            .SetBorder(new SolidBorder(Black, 0.5f));
        vc.Add(new Paragraph(value).SetFont(_regularFont).SetFontSize(8).SetFontColor(Black));
        table.AddCell(vc);
    }

    // ────────────────────────────────────────────────────────────────
    // 5. ADDRESS BOXES
    // ────────────────────────────────────────────────────────────────
    private void AddAddressBoxes(Document document, InvoiceDto invoice)
    {
        var table = new Table(new float[] { 1f, 0.05f, 1f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(5);

        // --- INVOICE ADDRESS (left) ---
        var invCell = new Cell().SetBorder(new SolidBorder(Black, 1)).SetPadding(0);

        // Header bar – white bg, black text
        var headerBar = new Table(1).SetWidth(UnitValue.CreatePercentValue(100));
        var hb = new Cell().SetBackgroundColor(White).SetPadding(3).SetPaddingLeft(6)
            .SetBorder(Border.NO_BORDER);
        hb.Add(new Paragraph("INVOICE ADDRESS").SetFont(_boldFont).SetFontSize(7.5f).SetFontColor(Black));
        headerBar.AddCell(hb);
        invCell.Add(headerBar);

        // Address fields
        var addrDiv = new Div().SetPadding(6).SetPaddingTop(4);
        addrDiv.Add(AddrLine("Customer Name:", invoice.CardName ?? "-"));
        addrDiv.Add(AddrLine("Customer Address:", ""));
        addrDiv.Add(AddrLine("Customer Address:", ""));
        addrDiv.Add(AddrLine("VAT NO:", ""));
        addrDiv.Add(AddrLine("TIN NUMBER", ""));
        invCell.Add(addrDiv);

        table.AddCell(invCell);

        // --- Spacer column ---
        table.AddCell(NoBorderCell());

        // --- DELIVERY ADDRESS (right) ---
        var delCell = new Cell().SetBorder(new SolidBorder(Black, 1)).SetPadding(0);

        // Header (not a black bar, just bold text with padding)
        var delHeaderDiv = new Div().SetPadding(6).SetPaddingBottom(0);
        delHeaderDiv.Add(new Paragraph("DELIVERY ADDRESS")
            .SetFont(_boldFont).SetFontSize(8).SetFontColor(Black).SetMarginBottom(0));
        delCell.Add(delHeaderDiv);

        // Empty space for address
        delCell.Add(new Div().SetMinHeight(55));

        // "Contact Details:" at bottom
        var contactDiv = new Div().SetPadding(6).SetPaddingTop(0);
        contactDiv.Add(new Paragraph("Contact Details:")
            .SetFont(_boldFont).SetFontSize(7).SetFontColor(Black));
        delCell.Add(contactDiv);

        table.AddCell(delCell);

        document.Add(table);
    }

    // ────────────────────────────────────────────────────────────────
    // 6. LINE ITEMS TABLE
    // ────────────────────────────────────────────────────────────────
    private void AddLineItemsTable(Document document, InvoiceDto invoice)
    {
        // Proportions: Item Code | Quantity | Service Description | Unit Price | Total Exc | VAT | Total Inc
        var table = new Table(new float[] { 1.1f, 0.8f, 3.2f, 0.9f, 0.9f, 0.8f, 0.9f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(0);

        // Header row – white bg, black text
        string[] headers = { "Item Code", "Quantity", "Service Description", "Unit Price", "Total Exc", "VAT", "Total Inc" };
        foreach (var h in headers)
        {
            var hc = new Cell().SetBackgroundColor(White)
                .SetPadding(4).SetPaddingLeft(3)
                .SetBorder(new SolidBorder(Black, 0.5f));
            hc.Add(new Paragraph(h).SetFont(_boldFont).SetFontSize(7).SetFontColor(Black));
            table.AddCell(hc);
        }

        // Data rows
        int dataRows = 0;
        if (invoice.Lines != null && invoice.Lines.Count > 0)
        {
            var lineTotalSum = invoice.Lines.Sum(l => l.LineTotal);

            foreach (var line in invoice.Lines)
            {
                var totalExc = line.LineTotal;

                // Proportional VAT from invoice-level VatSum
                decimal vatAmount;
                if (invoice.VatSum > 0 && lineTotalSum > 0)
                {
                    vatAmount = Math.Round(invoice.VatSum * (totalExc / lineTotalSum), 2);
                }
                else
                {
                    vatAmount = Math.Round(totalExc * 0.15m, 2);
                }
                var totalInc = totalExc + vatAmount;

                ItemCell(table, line.ItemCode ?? "-", TextAlignment.LEFT);
                ItemCell(table, line.Quantity.ToString("G29"), TextAlignment.LEFT);
                ItemCell(table, line.ItemDescription ?? "-", TextAlignment.LEFT);
                ItemCell(table, line.UnitPrice.ToString("N2"), TextAlignment.RIGHT);
                ItemCell(table, totalExc.ToString("N2"), TextAlignment.RIGHT);
                ItemCell(table, vatAmount.ToString("N2"), TextAlignment.RIGHT);
                ItemCell(table, totalInc.ToString("N2"), TextAlignment.RIGHT);
                dataRows++;
            }
        }

        // Fill remaining empty rows so the table area is consistent
        int minRows = 10;
        for (int i = dataRows; i < minRows; i++)
        {
            for (int j = 0; j < 7; j++)
            {
                var ec = new Cell().SetPadding(2).SetMinHeight(12)
                    .SetBorder(Border.NO_BORDER);
                ec.Add(new Paragraph("").SetFontSize(7));
                table.AddCell(ec);
            }
        }

        document.Add(table);
    }

    private void ItemCell(Table table, string text, TextAlignment align)
    {
        var cell = new Cell().SetPadding(2).SetPaddingLeft(3)
            .SetBorder(Border.NO_BORDER);
        cell.Add(new Paragraph(text)
            .SetFont(_regularFont).SetFontSize(7).SetFontColor(Black)
            .SetTextAlignment(align));
        table.AddCell(cell);
    }

    // ────────────────────────────────────────────────────────────────
    // 7. BANK DETAILS + TOTALS
    // ────────────────────────────────────────────────────────────────
    private void AddBankAndTotals(Document document, InvoiceDto invoice)
    {
        var outer = new Table(new float[] { 1.6f, 1f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginTop(4)
            .SetMarginBottom(2);

        // ── Left: bank deposit info ──
        var bankCell = NoBorderCell().SetPaddingRight(15).SetPaddingTop(6);

        bankCell.Add(new Paragraph("Please deposit into:")
            .SetFont(_boldFont).SetFontSize(8).SetFontColor(RedText).SetMarginBottom(4));

        var bt = new Table(new float[] { 1.8f, 3f }).SetWidth(UnitValue.CreatePercentValue(75));
        BankRow(bt, "Bank:", "Stanbic Bank Zimbabwe");
        BankRow(bt, "Account Name:", "Kefalos Cheese Products");
        BankRow(bt, "Account Number:", "9140000966435");
        BankRow(bt, "Branch:", "Belgravia");
        BankRow(bt, "Currency:", "USD");
        bankCell.Add(bt);

        outer.AddCell(bankCell);

        // ── Right: totals table ──
        var totalsCell = NoBorderCell();

        var tt = new Table(new float[] { 2f, 1.5f }).SetWidth(UnitValue.CreatePercentValue(100));

        var netTotal = invoice.DocTotal - invoice.VatSum;
        var discount = 0m;
        var freight = 0m;

        // Calculate discount from line-level discounts
        if (invoice.Lines != null)
        {
            foreach (var line in invoice.Lines)
            {
                if (line.DiscountPercent > 0)
                {
                    discount += (line.UnitPrice * line.Quantity) * (line.DiscountPercent / 100m);
                }
            }
        }

        TotalRow(tt, "Net Total", netTotal.ToString("N2"));
        TotalRow(tt, "Discount", discount.ToString("N2"));
        TotalRow(tt, "Freight", freight.ToString("N2"));
        TotalRow(tt, "Total EXC VAT", netTotal.ToString("N2"));
        TotalRow(tt, "VAT Total", invoice.VatSum.ToString("N2"));

        // Invoice Total – white bg, black text
        var itLabel = new Cell().SetBackgroundColor(White)
            .SetPadding(4).SetPaddingLeft(5)
            .SetBorder(new SolidBorder(Black, 0.5f));
        itLabel.Add(new Paragraph("Invoice Total").SetFont(_boldFont).SetFontSize(8).SetFontColor(Black));
        tt.AddCell(itLabel);

        var currency = invoice.DocCurrency ?? "USD";
        var itValue = new Cell().SetPadding(4)
            .SetBorder(new SolidBorder(Black, 0.5f));
        var tp = new Paragraph();
        tp.Add(new Text(currency).SetFont(_boldFont).SetFontSize(7).SetFontColor(Black));
        tp.Add(new Text(" " + invoice.DocTotal.ToString("N2")).SetFont(_boldFont).SetFontSize(8).SetFontColor(Black));
        tp.SetTextAlignment(TextAlignment.RIGHT);
        itValue.Add(tp);
        tt.AddCell(itValue);

        totalsCell.Add(tt);
        outer.AddCell(totalsCell);

        document.Add(outer);
    }

    private void BankRow(Table table, string label, string value)
    {
        var lc = NoBorderCell().SetPaddingBottom(1);
        lc.Add(new Paragraph(label).SetFont(_boldFont).SetFontSize(7).SetFontColor(Black));
        table.AddCell(lc);

        var vc = NoBorderCell().SetPaddingBottom(1);
        vc.Add(new Paragraph(value).SetFont(_regularFont).SetFontSize(7).SetFontColor(Black));
        table.AddCell(vc);
    }

    private void TotalRow(Table table, string label, string value)
    {
        var lc = new Cell().SetPadding(3).SetPaddingLeft(5)
            .SetBorder(new SolidBorder(Black, 0.5f));
        lc.Add(new Paragraph(label).SetFont(_boldFont).SetFontSize(8).SetFontColor(Black));
        table.AddCell(lc);

        var vc = new Cell().SetPadding(3)
            .SetBorder(new SolidBorder(Black, 0.5f));
        vc.Add(new Paragraph(value).SetFont(_regularFont).SetFontSize(8).SetFontColor(Black)
            .SetTextAlignment(TextAlignment.RIGHT));
        table.AddCell(vc);
    }

    // ────────────────────────────────────────────────────────────────
    // 8. CUSTOMER COPY LABEL
    // ────────────────────────────────────────────────────────────────
    private void AddCustomerCopyLabel(Document document)
    {
        document.Add(new Paragraph("CUSTOMER COPY")
            .SetFont(_boldFont)
            .SetFontSize(16)
            .SetFontColor(RedText)
            .SetTextAlignment(TextAlignment.CENTER)
            .SetMarginTop(2));
    }

    // ════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════

    private Cell NoBorderCell()
    {
        return new Cell().SetBorder(Border.NO_BORDER).SetPadding(0);
    }

    private Paragraph Para(string text, PdfFont font, float size, TextAlignment align)
    {
        return new Paragraph(text).SetFont(font).SetFontSize(size).SetFontColor(Black).SetTextAlignment(align);
    }

    private Paragraph SmallLine(string text)
    {
        return new Paragraph(text)
            .SetFont(_regularFont).SetFontSize(6.5f).SetFontColor(Black).SetMarginBottom(0.5f);
    }

    private Paragraph AddrLine(string label, string value)
    {
        var p = new Paragraph();
        p.Add(new Text(label + "   ").SetFont(_boldFont).SetFontSize(7));
        p.Add(new Text(value).SetFont(_regularFont).SetFontSize(7));
        p.SetMarginBottom(2);
        return p;
    }

    private void AddFallbackLogo(Cell cell)
    {
        cell.Add(new Paragraph("KEFALOS")
            .SetFont(_boldFont).SetFontSize(20).SetFontColor(Black).SetMarginBottom(0));
        cell.Add(new Paragraph("QUALITY DAIRY PRODUCE")
            .SetFont(_regularFont).SetFontSize(5.5f).SetFontColor(Black));
    }

    private string FormatDate(string? docDate)
    {
        if (string.IsNullOrEmpty(docDate)) return "-";
        if (DateTime.TryParse(docDate, out var dt))
            return dt.ToString("dd/MM/yy");
        return docDate;
    }
}
