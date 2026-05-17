using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;
using ShopInventory.DTOs;

namespace ShopInventory.Services;

public class QuotationPdfService(ILogger<QuotationPdfService> logger) : IQuotationPdfService
{
    private PdfFont _boldFont = null!;
    private PdfFont _regularFont = null!;

    private static readonly Color Black = new DeviceRgb(0, 0, 0);
    private static readonly Color White = new DeviceRgb(255, 255, 255);
    private static readonly Color RedText = new DeviceRgb(200, 0, 0);

    public async Task<byte[]> GenerateQuotationPdfAsync(
        QuotationDto quotation,
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
            document.SetMargins(20, 30, 25, 30);

            AddPageHeader(document);
            AddHorizontalRule(document);
            AddCompanyHeader(document);
            AddVatAndDocInfo(document, quotation);
            AddAddressBoxes(document, quotation, customerVatNo, customerTinNumber, customerPhone, customerEmail);
            AddLineItemsTable(document, quotation);
            AddBankAndTotals(document, quotation);
            AddCustomerCopyLabel(document);

            document.Close();

            return await Task.FromResult(memoryStream.ToArray());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating quotation PDF for {QuotationNumber}", quotation.QuotationNumber);
            throw;
        }
    }

    private void InitializeFonts()
    {
        _boldFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
        _regularFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
    }

    private void AddPageHeader(Document document)
    {
        var table = new Table(new float[] { 4f, 1.2f, 0.5f, 0.5f, 0.5f, 0.5f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(3);

        var titleCell = NoBorderCell();
        titleCell.Add(new Paragraph("Sales Quotation")
            .SetFont(_boldFont)
            .SetFontSize(14)
            .SetFontColor(Black)
            .SetTextAlignment(TextAlignment.CENTER));
        table.AddCell(titleCell);

        table.AddCell(NoBorderCell().Add(Para("PAGE", _boldFont, 8, TextAlignment.RIGHT)));
        table.AddCell(NoBorderCell().Add(Para("1", _boldFont, 8, TextAlignment.CENTER)));
        table.AddCell(NoBorderCell().Add(Para("OF", _regularFont, 8, TextAlignment.CENTER)));
        table.AddCell(NoBorderCell().Add(Para("1", _boldFont, 8, TextAlignment.CENTER)));
        table.AddCell(NoBorderCell());

        document.Add(table);
    }

    private void AddHorizontalRule(Document document)
    {
        var table = new Table(1).SetWidth(UnitValue.CreatePercentValue(100)).SetMarginBottom(6);
        var cell = new Cell().SetHeight(1).SetBackgroundColor(Black).SetBorder(Border.NO_BORDER).SetPadding(0);
        table.AddCell(cell);
        document.Add(table);
    }

    private void AddCompanyHeader(Document document)
    {
        document.Add(new Paragraph("KEFALOS CHEESE PRODUCTS PVT (LTD)")
            .SetFont(_boldFont)
            .SetFontSize(10)
            .SetFontColor(Black)
            .SetTextAlignment(TextAlignment.CENTER)
            .SetMarginBottom(4));

        var table = new Table(new float[] { 1.5f, 2.2f, 2.3f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(6);

        var logoCell = NoBorderCell().SetVerticalAlignment(VerticalAlignment.MIDDLE).SetPaddingRight(5);
        try
        {
            var logoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "kefalos-logo.jpg");
            if (File.Exists(logoPath))
            {
                var image = new Image(ImageDataFactory.Create(logoPath))
                    .SetWidth(105)
                    .SetAutoScale(true);
                logoCell.Add(image);
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

        var adminCell = NoBorderCell().SetVerticalAlignment(VerticalAlignment.TOP);
        adminCell.Add(new Paragraph("ADMINISTRATION OFFICE")
            .SetFont(_boldFont).SetFontSize(7).SetFontColor(Black).SetMarginBottom(2));
        adminCell.Add(SmallLine("35C Kingsmead Road, Borrowdale"));
        adminCell.Add(SmallLine("Harare, Zimbabwe"));
        adminCell.Add(SmallLine("Tel: +263/242 764 301/02/03"));
        adminCell.Add(SmallLine("Email : marketing@kefaloscheese.com"));
        adminCell.Add(SmallLine("Website: www.kefalosfood.com"));
        table.AddCell(adminCell);

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

    private void AddVatAndDocInfo(Document document, QuotationDto quotation)
    {
        var outer = new Table(new float[] { 1.1f, 1f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(6);

        var vatCell = NoBorderCell().SetVerticalAlignment(VerticalAlignment.MIDDLE);
        vatCell.Add(new Paragraph("VAT Reg: 220140892")
            .SetFont(_boldFont).SetFontSize(8).SetTextAlignment(TextAlignment.CENTER).SetMarginBottom(1));
        vatCell.Add(new Paragraph("TIN No: 2000022395")
            .SetFont(_boldFont).SetFontSize(8).SetTextAlignment(TextAlignment.CENTER));
        outer.AddCell(vatCell);

        var docCell = NoBorderCell();
        var details = new Table(new float[] { 2f, 2.5f }).SetWidth(UnitValue.CreatePercentValue(100));

        AddDocRow(details, "QUOTE NUMBER", quotation.QuotationNumber);
        AddDocRow(details, "REF", quotation.CustomerRefNo ?? quotation.CardName ?? "-");
        AddDocRow(details, "DATE", FormatDate(quotation.QuotationDate));
        AddDocRow(details, "VALID UNTIL", FormatDate(quotation.ValidUntil));

        docCell.Add(details);
        outer.AddCell(docCell);

        document.Add(outer);
    }

    private void AddDocRow(Table table, string label, string value)
    {
        var labelCell = new Cell().SetBackgroundColor(White)
            .SetPadding(3).SetPaddingLeft(6)
            .SetBorder(new SolidBorder(Black, 0.5f));
        labelCell.Add(new Paragraph(label).SetFont(_boldFont).SetFontSize(8).SetFontColor(Black));
        table.AddCell(labelCell);

        var valueCell = new Cell().SetPadding(3).SetPaddingLeft(6)
            .SetBorder(new SolidBorder(Black, 0.5f));
        valueCell.Add(new Paragraph(value).SetFont(_regularFont).SetFontSize(8).SetFontColor(Black));
        table.AddCell(valueCell);
    }

    private void AddAddressBoxes(
        Document document,
        QuotationDto quotation,
        string? customerVatNo,
        string? customerTinNumber,
        string? customerPhone,
        string? customerEmail)
    {
        var table = new Table(new float[] { 1f, 0.05f, 1f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(5);

        var billingCell = new Cell().SetBorder(new SolidBorder(Black, 1)).SetPadding(0);
        var billingHeader = new Table(1).SetWidth(UnitValue.CreatePercentValue(100));
        var billingHeaderCell = new Cell().SetBackgroundColor(White).SetPadding(3).SetPaddingLeft(6)
            .SetBorder(Border.NO_BORDER);
        billingHeaderCell.Add(new Paragraph("CUSTOMER ADDRESS").SetFont(_boldFont).SetFontSize(7.5f).SetFontColor(Black));
        billingHeader.AddCell(billingHeaderCell);
        billingCell.Add(billingHeader);

        var addressDiv = new Div().SetPadding(6).SetPaddingTop(4);
        addressDiv.Add(AddrLine("Customer Name:", quotation.CardName ?? quotation.CardCode));
        addressDiv.Add(AddrLine("Customer Address:", FormatFullAddress(quotation.BillToAddress)));
        addressDiv.Add(AddrLine("VAT NO:", customerVatNo ?? string.Empty));
        addressDiv.Add(AddrLine("TIN NUMBER", customerTinNumber ?? string.Empty));
        billingCell.Add(addressDiv);
        table.AddCell(billingCell);

        table.AddCell(NoBorderCell());

        var deliveryCell = new Cell().SetBorder(new SolidBorder(Black, 1)).SetPadding(0);
        var deliveryHeader = new Div().SetPadding(6).SetPaddingBottom(0);
        deliveryHeader.Add(new Paragraph("DELIVERY ADDRESS")
            .SetFont(_boldFont).SetFontSize(8).SetFontColor(Black).SetMarginBottom(0));
        deliveryCell.Add(deliveryHeader);

        var deliveryAddressDiv = new Div().SetPadding(6).SetPaddingTop(2).SetPaddingBottom(0);
        if (!string.IsNullOrWhiteSpace(quotation.ShipToAddress))
        {
            var shipLines = quotation.ShipToAddress.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in shipLines)
            {
                deliveryAddressDiv.Add(new Paragraph(line.Trim())
                    .SetFont(_regularFont).SetFontSize(7).SetFontColor(Black).SetMarginBottom(1));
            }
        }
        else
        {
            deliveryAddressDiv.SetMinHeight(40);
        }
        deliveryCell.Add(deliveryAddressDiv);

        var contactDiv = new Div().SetPadding(6).SetPaddingTop(0);
        contactDiv.Add(new Paragraph("Contact Details:")
            .SetFont(_boldFont).SetFontSize(7).SetFontColor(Black).SetMarginBottom(1));
        var contactText = BuildContactDetails(customerPhone, customerEmail);
        if (!string.IsNullOrWhiteSpace(contactText))
        {
            contactDiv.Add(new Paragraph(contactText)
                .SetFont(_regularFont).SetFontSize(7).SetFontColor(Black));
        }
        deliveryCell.Add(contactDiv);

        table.AddCell(deliveryCell);
        document.Add(table);
    }

    private void AddLineItemsTable(Document document, QuotationDto quotation)
    {
        var table = new Table(new float[] { 1.1f, 0.8f, 3.2f, 0.9f, 0.9f, 0.8f, 0.9f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginBottom(0);

        string[] headers = { "Item Code", "Quantity", "Service Description", "Unit Price", "Total Exc", "VAT", "Total Inc" };
        foreach (var header in headers)
        {
            var headerCell = new Cell().SetBackgroundColor(White)
                .SetPadding(4).SetPaddingLeft(3)
                .SetBorder(new SolidBorder(Black, 0.5f));
            headerCell.Add(new Paragraph(header).SetFont(_boldFont).SetFontSize(7).SetFontColor(Black));
            table.AddCell(headerCell);
        }

        foreach (var line in quotation.Lines)
        {
            var totalExc = line.LineTotal;
            var vatAmount = Math.Round(totalExc * line.TaxPercent / 100m, 2);
            var totalInc = totalExc + vatAmount;

            ItemCell(table, line.ItemCode, TextAlignment.LEFT);
            ItemCell(table, line.Quantity.ToString("G29"), TextAlignment.LEFT);
            ItemCell(table, line.ItemDescription ?? "-", TextAlignment.LEFT);
            ItemCell(table, line.UnitPrice.ToString("N2"), TextAlignment.RIGHT);
            ItemCell(table, totalExc.ToString("N2"), TextAlignment.RIGHT);
            ItemCell(table, vatAmount.ToString("N2"), TextAlignment.RIGHT);
            ItemCell(table, totalInc.ToString("N2"), TextAlignment.RIGHT);
        }

        document.Add(table);
    }

    private void ItemCell(Table table, string text, TextAlignment alignment)
    {
        var cell = new Cell().SetPadding(2).SetPaddingLeft(3)
            .SetBorder(Border.NO_BORDER);
        cell.Add(new Paragraph(text)
            .SetFont(_regularFont).SetFontSize(7).SetFontColor(Black)
            .SetTextAlignment(alignment));
        table.AddCell(cell);
    }

    private void AddBankAndTotals(Document document, QuotationDto quotation)
    {
        var outer = new Table(new float[] { 1.6f, 1f })
            .SetWidth(UnitValue.CreatePercentValue(100))
            .SetMarginTop(4)
            .SetMarginBottom(2);

        var bankCell = NoBorderCell().SetPaddingRight(15).SetPaddingTop(6);
        bankCell.Add(new Paragraph("Please deposit into:")
            .SetFont(_boldFont).SetFontSize(8).SetFontColor(RedText).SetMarginBottom(4));

        var bankTable = new Table(new float[] { 1.8f, 3f }).SetWidth(UnitValue.CreatePercentValue(75));
        BankRow(bankTable, "Bank:", "Stanbic Bank Zimbabwe");
        BankRow(bankTable, "Account Name:", "Kefalos Cheese Products");
        BankRow(bankTable, "Account Number:", "9140000966435");
        BankRow(bankTable, "Branch:", "Belgravia");
        BankRow(bankTable, "Currency:", quotation.Currency ?? "USD");
        bankCell.Add(bankTable);
        outer.AddCell(bankCell);

        var totalsCell = NoBorderCell();
        var totalsTable = new Table(new float[] { 2f, 1.5f }).SetWidth(UnitValue.CreatePercentValue(100));

        var discount = quotation.DiscountAmount;
        var freight = 0m;
        var totalExVat = quotation.SubTotal - discount;

        TotalRow(totalsTable, "Net Total", quotation.SubTotal.ToString("N2"));
        TotalRow(totalsTable, "Discount", discount.ToString("N2"));
        TotalRow(totalsTable, "Freight", freight.ToString("N2"));
        TotalRow(totalsTable, "Total EXC VAT", totalExVat.ToString("N2"));
        TotalRow(totalsTable, "VAT Total", quotation.TaxAmount.ToString("N2"));

        var totalLabelCell = new Cell().SetBackgroundColor(White)
            .SetPadding(4).SetPaddingLeft(5)
            .SetBorder(new SolidBorder(Black, 0.5f));
        totalLabelCell.Add(new Paragraph("Quotation Total").SetFont(_boldFont).SetFontSize(8).SetFontColor(Black));
        totalsTable.AddCell(totalLabelCell);

        var totalValueCell = new Cell().SetPadding(4)
            .SetBorder(new SolidBorder(Black, 0.5f));
        var totalParagraph = new Paragraph();
        totalParagraph.Add(new Text(quotation.Currency ?? "USD").SetFont(_boldFont).SetFontSize(7).SetFontColor(Black));
        totalParagraph.Add(new Text(" " + quotation.DocTotal.ToString("N2")).SetFont(_boldFont).SetFontSize(8).SetFontColor(Black));
        totalParagraph.SetTextAlignment(TextAlignment.RIGHT);
        totalValueCell.Add(totalParagraph);
        totalsTable.AddCell(totalValueCell);

        totalsCell.Add(totalsTable);
        outer.AddCell(totalsCell);

        document.Add(outer);
    }

    private void BankRow(Table table, string label, string value)
    {
        var labelCell = NoBorderCell().SetPaddingBottom(1);
        labelCell.Add(new Paragraph(label).SetFont(_boldFont).SetFontSize(7).SetFontColor(Black));
        table.AddCell(labelCell);

        var valueCell = NoBorderCell().SetPaddingBottom(1);
        valueCell.Add(new Paragraph(value).SetFont(_regularFont).SetFontSize(7).SetFontColor(Black));
        table.AddCell(valueCell);
    }

    private void TotalRow(Table table, string label, string value)
    {
        var labelCell = new Cell().SetPadding(3).SetPaddingLeft(5)
            .SetBorder(new SolidBorder(Black, 0.5f));
        labelCell.Add(new Paragraph(label).SetFont(_boldFont).SetFontSize(8).SetFontColor(Black));
        table.AddCell(labelCell);

        var valueCell = new Cell().SetPadding(3)
            .SetBorder(new SolidBorder(Black, 0.5f));
        valueCell.Add(new Paragraph(value).SetFont(_regularFont).SetFontSize(8).SetFontColor(Black)
            .SetTextAlignment(TextAlignment.RIGHT));
        table.AddCell(valueCell);
    }

    private void AddCustomerCopyLabel(Document document)
    {
        document.Add(new Paragraph("QUOTATION COPY")
            .SetFont(_boldFont)
            .SetFontSize(16)
            .SetFontColor(RedText)
            .SetTextAlignment(TextAlignment.CENTER)
            .SetMarginTop(2));
    }

    private Cell NoBorderCell()
    {
        return new Cell().SetBorder(Border.NO_BORDER).SetPadding(0);
    }

    private Paragraph Para(string text, PdfFont font, float size, TextAlignment alignment)
    {
        return new Paragraph(text).SetFont(font).SetFontSize(size).SetFontColor(Black).SetTextAlignment(alignment);
    }

    private Paragraph SmallLine(string text)
    {
        return new Paragraph(text)
            .SetFont(_regularFont).SetFontSize(6.5f).SetFontColor(Black).SetMarginBottom(0.5f);
    }

    private Paragraph AddrLine(string label, string value)
    {
        var paragraph = new Paragraph();
        paragraph.Add(new Text(label + "   ").SetFont(_boldFont).SetFontSize(7));
        paragraph.Add(new Text(value).SetFont(_regularFont).SetFontSize(7));
        paragraph.SetMarginBottom(2);
        return paragraph;
    }

    private void AddFallbackLogo(Cell cell)
    {
        cell.Add(new Paragraph("KEFALOS")
            .SetFont(_boldFont).SetFontSize(20).SetFontColor(Black).SetMarginBottom(0));
        cell.Add(new Paragraph("QUALITY DAIRY PRODUCE")
            .SetFont(_regularFont).SetFontSize(5.5f).SetFontColor(Black));
    }

    private string FormatFullAddress(string? fullAddress)
    {
        if (string.IsNullOrWhiteSpace(fullAddress))
        {
            return string.Empty;
        }

        var lines = fullAddress.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(", ", lines.Select(line => line.Trim()).Where(line => line.Length > 0));
    }

    private string BuildContactDetails(string? phone, string? email)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(phone))
        {
            parts.Add(phone);
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            parts.Add(email);
        }

        return string.Join(" | ", parts);
    }

    private string FormatDate(DateTime? date)
    {
        return date?.ToString("dd/MM/yy") ?? "-";
    }
}