using System.Globalization;
using iText.Barcodes;
using iText.IO.Font.Constants;
using iText.IO.Font.Otf;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Layout;
using iText.Layout.Properties;
using iText.Layout.Renderer;
using iText.Layout.Splitting;
using ShopInventory.DTOs;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Services;

public interface IInvoicePdfService
{
    Task<byte[]> GenerateInvoicePdfAsync(InvoiceDto invoice, string? fiscalQrCode = null);
}

/// <summary>
/// Renders an SAP invoice as the Fiscal Tax Invoice PDF.
/// </summary>
/// <remarks>
/// Built from <c>Fiscal Tax Invoice.dc.html</c> (claude.ai design project
/// 897f4900-e29e-417a-b893-d34ae6031bc7): one A4 sheet, with the fiscal block — QR, verification
/// code, fiscal day and device — beside the bank details and totals at the foot of the page.
///
/// Every length is written in the design's CSS pixels and converted by <see cref="Px"/>, so a value
/// can be checked against the design by reading it rather than by re-measuring the output. A4 is
/// 794 CSS px across and 595.28 pt, and a CSS pixel is exactly 0.75 pt.
///
/// The design sets Inter. It is not embedded here, so the design's own fallback, Helvetica, stands
/// in, and the design's 600 and 700 weights both land on Helvetica-Bold.
///
/// An invoice with more lines than one sheet holds keeps flowing: the item table repeats its head on
/// every page, the title rule and page count are stamped on each, and the footer sits at the bottom
/// of the last.
/// </remarks>
public class InvoicePdfService : IInvoicePdfService
{
    private readonly ILogger<InvoicePdfService> _logger;

    public InvoicePdfService(ILogger<InvoicePdfService> logger)
    {
        _logger = logger;
    }

    public Task<byte[]> GenerateInvoicePdfAsync(InvoiceDto invoice, string? fiscalQrCode = null)
    {
        try
        {
            using var memoryStream = new MemoryStream();
            var writer = new PdfWriter(memoryStream);
            writer.SetCloseStream(false);
            var pdf = new PdfDocument(writer);

            // Not flushed page by page: the page count and where the footer lands are only known once
            // the item table has been laid out, and both are stamped onto pages already filled.
            var document = new Document(pdf, PageSize.A4, false);

            new InvoiceSheet(pdf, document, _logger).Render(invoice, fiscalQrCode);

            document.Close();

            return Task.FromResult(memoryStream.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating invoice PDF for DocEntry {DocEntry}: {Message}",
                invoice.DocEntry, ex.Message);
            throw;
        }
    }

    /// <summary>Design CSS pixels to PDF points.</summary>
    private static float Px(float cssPixels) => cssPixels * 0.75f;

    private sealed class InvoiceSheet
    {
        private const string DefaultVerificationSite = "https://fdms.zimra.co.zw";

        // The page's inner div: padding 40px 44px 36px.
        private static readonly float PageTop = Px(40);
        private static readonly float PageSide = Px(44);
        private static readonly float PageBottom = Px(36);
        private static readonly float ContentWidth = PageSize.A4.GetWidth() - 2 * PageSide;

        // Body text: 11px at line-height 1.45.
        private static readonly float BodySize = Px(11);
        private const float BodyLeading = 1.45f;

        private static readonly Color Ink = new DeviceRgb(0x14, 0x14, 0x1A);
        // Every value the document fills in, as opposed to the labels it always prints.
        private static readonly Color Filled = new DeviceRgb(0x7A, 0x20, 0x20);
        private static readonly Color Muted = new DeviceRgb(0x3A, 0x3A, 0x46);
        private static readonly Color LinkInk = new DeviceRgb(0x5A, 0x2A, 0x8F);
        private static readonly Color Hairline = new DeviceRgb(0xC9, 0xC9, 0xD2);

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private static readonly (string Label, string Value)[] DepositAccount =
        [
            ("Bank:", "Stanbic Bank Zimbabwe"),
            ("Account Name:", "Kefalos Cheese Products"),
            ("Account Number:", "9140005966435"),
            ("Branch:", "Belgravia"),
            ("Currency:", "USD"),
        ];

        private static readonly (string Header, float Percent, TextAlignment Align)[] ItemColumns =
        [
            ("Item Code", 13, TextAlignment.LEFT),
            ("Quantity", 10, TextAlignment.LEFT),
            ("Service Description", 32, TextAlignment.LEFT),
            ("Unit Price", 12, TextAlignment.RIGHT),
            ("Total Exc", 12, TextAlignment.RIGHT),
            ("VAT", 9, TextAlignment.RIGHT),
            ("Total Inc", 12, TextAlignment.RIGHT),
        ];

        private readonly PdfDocument _pdf;
        private readonly Document _document;
        private readonly ILogger _logger;
        private readonly PdfFont _bold = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
        private readonly PdfFont _regular = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

        public InvoiceSheet(PdfDocument pdf, Document document, ILogger logger)
        {
            _pdf = pdf;
            _document = document;
            _logger = logger;
        }

        public void Render(InvoiceDto invoice, string? fiscalQrCode)
        {
            var titleBandHeight = Measure(TitleBand(1, 1));

            // Content starts the design's 20px below the title rule on every page: the gap the company
            // name keeps on the first, and the one the repeated item table head needs on the rest.
            _document.SetMargins(PageTop + titleBandHeight + Px(20), PageSide, PageBottom, PageSide);

            _document.Add(CompanyName());
            _document.Add(CompanyHeader());
            _document.Add(DocumentDetails(invoice));
            _document.Add(AddressBoxes(invoice));
            _document.Add(LineItems(invoice));

            var pageCount = AddFooter(invoice, fiscalQrCode);

            var titleBandBottom = PageSize.A4.GetHeight() - PageTop - titleBandHeight;
            for (var page = 1; page <= pageCount; page++)
            {
                _document.Add(TitleBand(page, pageCount)
                    .SetFixedPosition(page, PageSide, titleBandBottom, ContentWidth));
            }
        }

        // ── Title band ──────────────────────────────────────────────────────

        /// <summary>The title, page count and heavy rule, stamped at the top of every page.</summary>
        private Table TitleBand(int page, int pageCount)
        {
            // grid-template-columns: 1fr auto 1fr — the title centres on the sheet, not in the space
            // left beside the page count.
            var band = new Table(UnitValue.CreatePercentArray([1f, 1f, 1f]))
                .SetWidth(ContentWidth)
                .SetFixedLayout();

            band.AddCell(TitleBandCell());
            band.AddCell(TitleBandCell().Add(
                Line("Fiscal Tax Invoice", _bold, Px(20), Ink)
                    .SetCharacterSpacing(Tracking(20, 0.01f))
                    .SetTextAlignment(TextAlignment.CENTER)));

            var count = new Table(2).SetHorizontalAlignment(HorizontalAlignment.RIGHT);
            count.AddCell(Bare().Add(PageCountLine($"PAGE  {page}")));
            count.AddCell(Bare().SetPaddingLeft(Px(18)).Add(PageCountLine($"OF  {pageCount}")));
            band.AddCell(TitleBandCell().Add(count));

            return band;
        }

        private static Cell TitleBandCell()
            => Bare()
                .SetBorderBottom(new SolidBorder(Ink, Px(1.5f)))
                .SetPaddingBottom(Px(10))
                .SetVerticalAlignment(VerticalAlignment.BOTTOM);

        private Paragraph PageCountLine(string text)
            => Line(text, _bold, Px(10), Muted).SetCharacterSpacing(Tracking(10, 0.1f));

        // ── Company ─────────────────────────────────────────────────────────

        private Paragraph CompanyName()
            => Line("KEFALOS CHEESE PRODUCTS PVT (LTD)", _bold, Px(14.5f), Ink)
                .SetCharacterSpacing(Tracking(14.5f, 0.02f))
                .SetTextAlignment(TextAlignment.CENTER);

        private Table CompanyHeader()
        {
            // grid-template-columns: 200px 1fr 1fr; gap 28px. A gap rides on the cell to its left.
            var officeWidth = (ContentWidth - Px(200) - 2 * Px(28)) / 2;
            var header = new Table(UnitValue.CreatePointArray([Px(200) + Px(28), officeWidth + Px(28), officeWidth]))
                .SetWidth(ContentWidth)
                .SetFixedLayout()
                .SetMarginTop(Px(14));

            header.AddCell(Bare().SetPaddingRight(Px(28)).Add(Logo()));
            header.AddCell(Bare().SetPaddingRight(Px(28)).Add(Office(
                "ADMINISTRATION OFFICE",
                "35C Kingsmead Road, Borrowdale",
                "Harare, Zimbabwe",
                "Tel: +263/242 764 301/02/03",
                "Email: marketing@kefaloscheese.com",
                "Website: www.kefalosfood.com")));
            header.AddCell(Bare().Add(Office(
                "FACTORY",
                "Bhara Bhara Farm, Mubaira Road",
                "Harare South, Harare, Zimbabwe",
                "Tel: +263 242 613 454/5/6/5",
                "Email: kefalos@kefaloscheese.com",
                "Facebook: www.facebook.com/kefalosproducts")));

            return header;
        }

        private Div Logo()
        {
            var logoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "kefalos-logo.jpg");

            if (File.Exists(logoPath))
            {
                try
                {
                    // A 200 × 100px slot, fit: contain.
                    return new Div().Add(new Image(ImageDataFactory.Create(logoPath)).ScaleToFit(Px(200), Px(100)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not load the invoice logo from {LogoPath}", logoPath);
                }
            }

            return new Div()
                .Add(Line("KEFALOS", _bold, Px(26), Ink))
                .Add(Line("QUALITY DAIRY PRODUCE", _regular, Px(7.5f), Ink));
        }

        private Div Office(string heading, params string[] lines)
        {
            var office = new Div().Add(
                Line(heading, _bold, Px(10.5f), Ink)
                    .SetCharacterSpacing(Tracking(10.5f, 0.06f))
                    .SetMarginBottom(Px(5)));

            for (var index = 0; index < lines.Length; index++)
            {
                office.Add(Body(lines[index], Filled).SetMarginTop(index == 0 ? 0 : Px(2)));
            }

            return office;
        }

        // ── Document numbers ────────────────────────────────────────────────

        private Table DocumentDetails(InvoiceDto invoice)
        {
            // grid-template-columns: 1fr 280px; gap 20px.
            var details = new Table(UnitValue.CreatePointArray([ContentWidth - Px(280), Px(280)]))
                .SetWidth(ContentWidth)
                .SetFixedLayout()
                .SetMarginTop(Px(18));

            details.AddCell(Bare()
                .SetPaddingRight(Px(20))
                .SetVerticalAlignment(VerticalAlignment.MIDDLE)
                .Add(Strong("VAT Reg: 220140892").SetTextAlignment(TextAlignment.RIGHT))
                .Add(Strong("TIN No: 2000022395").SetTextAlignment(TextAlignment.RIGHT).SetMarginTop(Px(4))));

            var docNumber = invoice.DocNum.ToString(Invariant);
            var numbers = new Table(UnitValue.CreatePointArray([Px(140), Px(140)]))
                .SetWidth(Px(280))
                .SetFixedLayout();

            DetailRow(numbers, "DOC NUMBER", docNumber);
            // REVMax files an SAP invoice under its DocNum, so the fiscal invoice number is the same figure.
            DetailRow(numbers, "INVOICE NUMBER", docNumber);
            DetailRow(numbers, "REF :", invoice.CardName ?? "-");
            DetailRow(numbers, "DATE", FormatDate(invoice.DocDate));

            details.AddCell(Bare().Add(numbers));
            return details;
        }

        private void DetailRow(Table table, string label, string value)
        {
            table.AddCell(Pad(Boxed(), 4, 8).Add(
                Line(label, _bold, Px(10), Ink).SetCharacterSpacing(Tracking(10, 0.02f))));
            table.AddCell(Pad(Boxed(), 4, 8).Add(Line(value, _regular, Px(10), Filled)));
        }

        // ── Addresses ───────────────────────────────────────────────────────

        private Table AddressBoxes(InvoiceDto invoice)
        {
            // grid-template-columns: 1fr 1fr; gap 16px.
            var boxWidth = (ContentWidth - Px(16)) / 2;
            var boxes = new Table(UnitValue.CreatePointArray([boxWidth, Px(16), boxWidth]))
                .SetWidth(ContentWidth)
                .SetFixedLayout()
                .SetMarginTop(Px(16));

            boxes.AddCell(AddressBox("INVOICE ADDRESS", boxWidth,
                ("Customer Name:", invoice.CardName),
                ("Customer Address:", JoinAddress(invoice.BillToAddress)),
                ("VAT NO:", invoice.CustomerVatNo),
                ("TIN NUMBER:", invoice.CustomerTinNumber)));

            boxes.AddCell(Bare());

            // The design draws the delivery box with contact details alone. A ship-to address, when
            // SAP holds one, keeps its place above them rather than silently leaving the invoice.
            var delivery = new List<(string Label, string? Value)>();
            if (!string.IsNullOrWhiteSpace(invoice.ShipToAddress))
            {
                delivery.Add(("Address:", JoinAddress(invoice.ShipToAddress)));
            }

            delivery.Add(("Contact Details:", JoinContact(invoice.CustomerPhone, invoice.CustomerEmail)));
            boxes.AddCell(AddressBox("DELIVERY ADDRESS", boxWidth, delivery.ToArray()));

            return boxes;
        }

        private Cell AddressBox(string heading, float width, params (string Label, string? Value)[] rows)
        {
            // padding 9px 12px 12px; the label column is max-content, then a 12px gap, then the value.
            var box = Boxed()
                .SetPaddingTop(Px(9))
                .SetPaddingLeft(Px(12))
                .SetPaddingRight(Px(12))
                .SetPaddingBottom(Px(12))
                .Add(Line(heading, _bold, Px(10.5f), Ink)
                    .SetCharacterSpacing(Tracking(10.5f, 0.06f))
                    .SetMarginBottom(Px(8)));

            var innerWidth = width - 2 * Px(12);
            var labelWidth = rows.Max(row => _bold.GetWidth(row.Label, BodySize)) + Px(12) + 1f;
            var grid = new Table(UnitValue.CreatePointArray([labelWidth, innerWidth - labelWidth]))
                .SetWidth(innerWidth)
                .SetFixedLayout();

            for (var index = 0; index < rows.Length; index++)
            {
                var rowGap = index == rows.Length - 1 ? 0 : Px(7);
                grid.AddCell(Bare().SetPaddingBottom(rowGap).Add(Strong(rows[index].Label)));
                grid.AddCell(Bare().SetPaddingBottom(rowGap).Add(Body(rows[index].Value ?? string.Empty, Filled)));
            }

            return box.Add(grid);
        }

        // ── Items ───────────────────────────────────────────────────────────

        private Table LineItems(InvoiceDto invoice)
        {
            var items = new Table(UnitValue.CreatePercentArray(ItemColumns.Select(column => column.Percent).ToArray()))
                .SetWidth(ContentWidth)
                .SetFixedLayout()
                .SetMarginTop(Px(18));

            foreach (var column in ItemColumns)
            {
                items.AddHeaderCell(Pad(Boxed(), 6, 8).Add(
                    Strong(column.Header)
                        .SetCharacterSpacing(Tracking(11, 0.03f))
                        .SetTextAlignment(column.Align)));
            }

            var lines = invoice.Lines ?? [];
            var netSum = lines.Sum(line => line.LineTotal);

            foreach (var line in lines)
            {
                var (vat, totalInc) = LineTax(line, netSum, invoice.VatSum);

                ItemCell(items, line.ItemCode ?? "-", Ink, TextAlignment.LEFT);
                ItemCell(items, line.Quantity.ToString("G29", Invariant), Ink, TextAlignment.LEFT);
                ItemCell(items, line.ItemDescription ?? "-", Filled, TextAlignment.LEFT);
                ItemCell(items, Money(line.UnitPrice), Ink, TextAlignment.RIGHT);
                ItemCell(items, Money(line.LineTotal), Ink, TextAlignment.RIGHT);
                ItemCell(items, Money(vat), Ink, TextAlignment.RIGHT);
                ItemCell(items, Money(totalInc), Ink, TextAlignment.RIGHT);
            }

            return items;
        }

        private void ItemCell(Table table, string text, Color color, TextAlignment align)
            => table.AddCell(Bare()
                .SetPadding(Px(8))
                .SetVerticalAlignment(VerticalAlignment.TOP)
                .Add(Body(text, color).SetTextAlignment(align)));

        /// <summary>A line's VAT and its VAT-inclusive total.</summary>
        /// <remarks>
        /// SAP's <c>GrossTotal</c> is the line after its discount with the line's own VAT group applied,
        /// so its difference from <c>LineTotal</c> is the VAT actually charged on that line — nothing on
        /// a zero-rated item. Spreading the document's VAT across lines by value put VAT on zero-rated
        /// lines whenever they shared an invoice with standard-rated ones, and a flat 15% guess stood
        /// in when the document carried no VAT at all. A line SAP returned without a gross total still
        /// takes its share by value, because no better figure exists for it.
        /// </remarks>
        private static (decimal Vat, decimal TotalInc) LineTax(InvoiceLineDto line, decimal netSum, decimal vatSum)
        {
            if (line.GrossTotal != 0m)
            {
                return (Math.Round(line.GrossTotal - line.LineTotal, 2), Math.Round(line.GrossTotal, 2));
            }

            var share = netSum != 0m ? Math.Round(vatSum * (line.LineTotal / netSum), 2) : 0m;
            return (share, line.LineTotal + share);
        }

        // ── Footer ──────────────────────────────────────────────────────────

        /// <summary>Places the footer at the bottom of the last page.</summary>
        /// <returns>The page the footer landed on, which is the document's last.</returns>
        private int AddFooter(InvoiceDto invoice, string? fiscalQrCode)
        {
            var footer = Footer(invoice, fiscalQrCode);
            var footerHeight = Measure(footer);

            var area = ((RootRenderer)_document.GetRenderer()).GetCurrentArea();
            var page = area.GetPageNumber();

            // The item region keeps at least 24px between its last row and the footer's hairline. When
            // the page the table ended on cannot hold the footer too, the footer takes the next one.
            if (area.GetBBox().GetHeight() < footerHeight + Px(24))
            {
                page++;
            }

            _document.Add(footer.SetFixedPosition(page, PageSide, PageBottom, ContentWidth));
            return page;
        }

        private Div Footer(InvoiceDto invoice, string? fiscalQrCode)
        {
            var fiscal = FiscalBlock(invoice, fiscalQrCode);
            var totals = TotalRows(invoice);

            // grid-template-columns: auto 1fr 320px; gap 24px; align-items: end. A 1fr column never
            // shrinks below its content, so when the bank details cannot fit beside a 320px totals
            // block the totals give way, down to their own content, rather than the account number
            // breaking mid-digit.
            var bankLabelWidth = DepositAccount.Max(row => _bold.GetWidth(row.Label, BodySize)) + Px(14);
            var bankContentWidth = bankLabelWidth + DepositAccount.Max(row => _regular.GetWidth(row.Value, BodySize)) + 1f;

            var totalsLabelWidth = totals.Max(row => _bold.GetWidth(row.Label, BodySize)) + 2 * Px(10) + 1f;
            var totalsValueWidth = Math.Max(
                Px(86) + 2 * Px(10),
                totals.Max(row => (row.Strong ? _bold : _regular).GetWidth(row.Value, BodySize)) + 2 * Px(10) + 1f);

            var fiscalWidth = fiscal is null ? 0f : Px(170) + Px(24);
            var besideFiscal = ContentWidth - fiscalWidth;
            var totalsWidth = Math.Min(
                Px(320),
                Math.Max(totalsLabelWidth + totalsValueWidth, besideFiscal - bankContentWidth - Px(24)));
            var bankWidth = besideFiscal - totalsWidth;

            var row = new Table(UnitValue.CreatePointArray(
                    fiscal is null ? [bankWidth, totalsWidth] : [fiscalWidth, bankWidth, totalsWidth]))
                .SetWidth(ContentWidth)
                .SetFixedLayout();

            if (fiscal is not null)
            {
                row.AddCell(FooterCell().SetPaddingRight(Px(24)).Add(fiscal));
            }

            row.AddCell(FooterCell().SetPaddingRight(Px(24)).Add(BankDetails(bankLabelWidth, bankWidth - Px(24))));
            row.AddCell(FooterCell().Add(Totals(totals, totalsWidth, totalsValueWidth)));

            // The item region's closing hairline, then the design's 18px down to the footer row.
            return new Div()
                .SetBorderTop(new SolidBorder(Hairline, Px(1)))
                .SetPaddingTop(Px(18))
                .Add(row);
        }

        private static Cell FooterCell() => Bare().SetVerticalAlignment(VerticalAlignment.BOTTOM);

        /// <summary>The QR and the receipt details printed under it, or null for an unfiscalised invoice.</summary>
        private Div? FiscalBlock(InvoiceDto invoice, string? fiscalQrCode)
        {
            var qrPayload = fiscalQrCode?.Trim();
            var verificationCode = DisplayVerificationCode(invoice.FiscalVerificationCode);

            if (string.IsNullOrEmpty(qrPayload) && verificationCode is null)
            {
                return null;
            }

            var block = new Div();

            if (!string.IsNullOrEmpty(qrPayload))
            {
                try
                {
                    var qrCode = new BarcodeQRCode(qrPayload);
                    block.Add(new Image(qrCode.CreateFormXObject(_pdf))
                        .ScaleAbsolute(Px(104), Px(104))
                        .SetMarginBottom(Px(7)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Could not render fiscal QR code on invoice PDF for DocEntry {DocEntry}",
                        invoice.DocEntry);
                }
            }

            FiscalLine(block, "Verification Code:", verificationCode);
            FiscalLine(block, "Fiscal Day:", invoice.FiscalDay);
            FiscalLine(block, "Device ID:", invoice.FiscalDeviceId);

            var site = VerificationSite(qrPayload);
            block.Add(new Paragraph()
                .Add(new Text("Verify this receipt manually at ").SetFont(_regular).SetFontSize(Px(10)).SetFontColor(Muted))
                .Add(new Link(site, PdfAction.CreateURI(site)).SetFont(_regular).SetFontSize(Px(10)).SetFontColor(LinkInk))
                .SetMultipliedLeading(1.4f)
                .SetMargin(0)
                .SetMarginTop(Px(3)));

            return block;
        }

        /// <summary>One labelled receipt detail; a detail the device did not report is left off, not printed blank.</summary>
        private void FiscalLine(Div block, string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            block.Add(new Paragraph()
                .Add(new Text(label + " ").SetFont(_bold).SetFontSize(Px(10)).SetFontColor(Ink))
                .Add(new Text(value.Trim())
                    .SetFont(_regular)
                    .SetFontSize(Px(10))
                    .SetFontColor(Filled)
                    // A verification code is read out over the phone; broken at a hyphen, the last group
                    // reads as a separate figure. When it does not fit beside its label it drops whole.
                    .SetSplitCharacters(KeepWhole.Instance))
                .SetMultipliedLeading(1.35f)
                .SetMargin(0)
                .SetMarginBottom(Px(3)));
        }

        private sealed class KeepWhole : ISplitCharacters
        {
            public static readonly KeepWhole Instance = new();

            public bool IsSplitCharacter(GlyphLine text, int glyphPos) => false;
        }

        /// <summary>REVMax may hand back the code raw or already grouped; print it grouped in fours either way.</summary>
        private static string? DisplayVerificationCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return null;
            }

            var trimmed = code.Trim();
            return trimmed.Contains('-') ? trimmed : FiscalReceiptQrComposer.FormatVerificationCode(trimmed);
        }

        /// <summary>
        /// The ZIMRA site to verify on: the host the receipt's own QR points at, so a test device's
        /// receipt names the test site rather than production.
        /// </summary>
        private static string VerificationSite(string? qrPayload)
            => Uri.TryCreate(qrPayload, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? uri.GetLeftPart(UriPartial.Authority)
                : DefaultVerificationSite;

        private Div BankDetails(float labelWidth, float width)
        {
            var grid = new Table(UnitValue.CreatePointArray([labelWidth, width - labelWidth]))
                .SetWidth(width)
                .SetFixedLayout();

            for (var index = 0; index < DepositAccount.Length; index++)
            {
                var rowGap = index == DepositAccount.Length - 1 ? 0 : Px(4);
                grid.AddCell(Bare().SetPaddingBottom(rowGap).Add(Strong(DepositAccount[index].Label)));
                grid.AddCell(Bare().SetPaddingBottom(rowGap).Add(Body(DepositAccount[index].Value, Filled)));
            }

            return new Div()
                .Add(Strong("Please deposit into:")
                    .SetCharacterSpacing(Tracking(11, 0.02f))
                    .SetMarginBottom(Px(7)))
                .Add(grid);
        }

        private static (string Label, string Value, bool Strong)[] TotalRows(InvoiceDto invoice)
        {
            var netTotal = invoice.DocTotal - invoice.VatSum;
            var discount = (invoice.Lines ?? [])
                .Where(line => line.DiscountPercent > 0)
                .Sum(line => line.UnitPrice * line.Quantity * (line.DiscountPercent / 100m));
            var currency = invoice.DocCurrency ?? "USD";

            return
            [
                ("Net Total", Money(netTotal), false),
                ("Discount", Money(discount), false),
                ("Freight", Money(0m), false),
                ("Total EXC VAT", Money(netTotal), false),
                ("VAT Total", Money(invoice.VatSum), false),
                ("Invoice Total", $"{currency} {Money(invoice.DocTotal)}", true),
            ];
        }

        private Table Totals((string Label, string Value, bool Strong)[] rows, float width, float valueWidth)
        {
            var totals = new Table(UnitValue.CreatePointArray([width - valueWidth, valueWidth]))
                .SetWidth(width)
                .SetFixedLayout();

            foreach (var row in rows)
            {
                totals.AddCell(Pad(Boxed(), 5, 10).Add(Strong(row.Label)));
                totals.AddCell(Pad(Boxed(), 5, 10).Add(
                    Line(row.Value, row.Strong ? _bold : _regular, BodySize, Ink).SetTextAlignment(TextAlignment.RIGHT)));
            }

            return totals;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>The height an element takes when laid out across the content width.</summary>
        private float Measure(IBlockElement element)
        {
            var renderer = element.CreateRendererSubTree().SetParent(_document.GetRenderer());
            var result = renderer.Layout(new LayoutContext(
                new LayoutArea(1, new Rectangle(ContentWidth, PageSize.A4.GetHeight()))));

            return result.GetOccupiedArea().GetBBox().GetHeight();
        }

        private static Paragraph Line(string text, PdfFont font, float size, Color color)
            => new Paragraph(text)
                .SetFont(font)
                .SetFontSize(size)
                .SetFontColor(color)
                .SetMultipliedLeading(BodyLeading)
                .SetMargin(0);

        private Paragraph Body(string text, Color color) => Line(text, _regular, BodySize, color);

        private Paragraph Strong(string text) => Line(text, _bold, BodySize, Ink);

        private static Cell Bare() => new Cell().SetBorder(Border.NO_BORDER).SetPadding(0);

        private static Cell Boxed() => new Cell().SetBorder(new SolidBorder(Ink, Px(1)));

        private static Cell Pad(Cell cell, float verticalPx, float horizontalPx)
            => cell
                .SetPaddingTop(Px(verticalPx))
                .SetPaddingBottom(Px(verticalPx))
                .SetPaddingLeft(Px(horizontalPx))
                .SetPaddingRight(Px(horizontalPx));

        /// <summary>CSS letter-spacing in em, for text set at <paramref name="sizePx"/>.</summary>
        private static float Tracking(float sizePx, float em) => Px(sizePx) * em;

        private static string Money(decimal value) => value.ToString("N2", Invariant);

        private static string JoinAddress(string? address)
            => string.IsNullOrWhiteSpace(address)
                ? string.Empty
                : string.Join(", ", address
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Trim())
                    .Where(part => part.Length > 0));

        private static string JoinContact(string? phone, string? email)
            => string.Join(" | ", new[] { phone, email }.Where(part => !string.IsNullOrWhiteSpace(part)));

        private static string FormatDate(string? docDate)
        {
            if (string.IsNullOrEmpty(docDate))
            {
                return "-";
            }

            return DateTime.TryParse(docDate, Invariant, DateTimeStyles.None, out var date)
                ? date.ToString("dd/MM/yy", Invariant)
                : docDate;
        }
    }
}
