using System.Globalization;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Slip;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The till slip an invoice is sent as when its sale was filed as a 48 mm receipt.
/// </summary>
/// <remarks>
/// The layout is asserted as text, the way the van app asserts the slip its printer gives: every line
/// fits the 32 columns, nothing is outside ASCII, the amounts end in the last column, and the figures
/// are the ones the A4 invoice prints for the same document. The PDF is then read back to prove the
/// page carries that text, on one narrow page.
///
/// Set <c>INVOICE_PDF_OUT</c> to a directory to keep every rendered slip for inspection.
/// </remarks>
public sealed class InvoiceSlipTests
{
    private const string QrPayload = "https://fdms.zimra.co.zw/000002286211092026000021687760A74CD961202377";

    private static readonly InvoiceSlipPdfService Service = new(NullLogger<InvoiceSlipPdfService>.Instance);

    [Fact]
    public void The_slip_reads_as_the_vans_printed_receipt_does()
    {
        var slip = InvoiceSlipLayout.Compose(VanInvoice(), QrPayload);
        var body = slip.Body.Select(line => line.Text).ToList();

        Assert.Equal(
            [
                "    KEFALOS CHEESE PRODUCTS",
                "       Admin: Harare, ZW",
                "   Factory: Bhara Bhara Farm,",
                "        Harare South, ZW",
                "VAT No                 220140892",
                "TIN No                2000022395",
                "================================",
                "       FISCAL TAX INVOICE",
                "Invoice                   789106",
                "Sale ref        VO-20261011-0007",
                "Date                  11/10/2026",
                "--------------------------------",
                "Buyer",
                "Mbare Tuck Shop",
                "Stand 12, Mbare, Harare",
                "VAT No 220000041",
                "--------------------------------",
                "Item                      Amount",
                "Superior White Cheddar 1kg",
                "  2 x 7.22                 14.44",
                "Full Cream Milk 500ml",
                "  3 x 3.08                  9.24",
                "--------------------------------",
                "Total excl. VAT            20.59",
                "VAT                         3.09",
                "================================",
                "TOTAL USD                  23.68",
                "================================"
            ],
            body);

        Assert.Equal(
            [
                "   Scan to verify with ZIMRA",
                "   or tap the code to open it",
                "--------------------------------",
                "       Verification code",
                "      60A7-4CD9-6120-2377",
                "",
                "Fiscal day                   128",
                "Receipt no                 21687",
                "Device ID                  22862",
                "",
                "  Thank you for your business!"
            ],
            slip.Footer.Select(line => line.Text).ToList());

        Assert.Equal(QrPayload, slip.QrPayload);
    }

    [Fact]
    public void The_title_and_the_total_are_the_tall_lines_and_nothing_else_is()
    {
        var slip = InvoiceSlipLayout.Compose(VanInvoice(), QrPayload);

        Assert.Equal(
            ["       FISCAL TAX INVOICE", "TOTAL USD                  23.68"],
            slip.Body.Concat(slip.Footer).Where(line => line.Style == SlipLineStyle.Tall).Select(line => line.Text).ToList());
    }

    [Fact]
    public void The_lines_add_up_to_the_total_and_every_figure_is_the_A4_invoices()
    {
        var invoice = VanInvoice();
        var slip = InvoiceSlipLayout.Compose(invoice, QrPayload);

        // Read back off the slip, the way a customer adding it up would.
        var amounts = slip.Body
            .Where(line => line.Text.StartsWith("  ", StringComparison.Ordinal) && line.Text.Contains(" x "))
            .Select(line => decimal.Parse(line.Text[^10..], CultureInfo.InvariantCulture))
            .ToList();
        var total = decimal.Parse(slip.Body.Single(line => line.Text.StartsWith("TOTAL", StringComparison.Ordinal)).Text[^10..], CultureInfo.InvariantCulture);

        Assert.Equal(invoice.DocTotal, total);
        Assert.Equal(total, amounts.Sum());

        var netSum = invoice.Lines!.Sum(line => line.LineTotal);
        Assert.Equal(
            invoice.Lines!.Select(line => InvoiceLineTax.Of(line, netSum, invoice.VatSum).TotalInc).ToList(),
            amounts);
    }

    [Fact]
    public void Nothing_runs_past_the_paper_however_awkward_the_invoice()
    {
        var invoice = VanInvoice();
        invoice.CardName = "Chitungwiza Wholesalers & Distributors (Private) Limited t/a “Mai Tatenda’s” Café";
        invoice.BillToAddress = "Shop 14B, Makoni Shopping Centre\r\nCorner Seke Road and Chaminuka Street\nChitungwiza";
        invoice.VanSaleOrderNumber = "VANSALESONLINE-20261011-0007-RETRY-3-AB12CD34EF56";
        invoice.CustomerVatNo = "220000041";
        invoice.CustomerTinNumber = "2000000041";
        invoice.DocTotal = 1234567.89m;
        invoice.VatSum = 161030.59m;
        invoice.Lines!.Add(new InvoiceLineDto
        {
            ItemCode = "X",
            ItemDescription = "Supercalifragilisticexpialidocious-extra-mature-vintage cheddar wheel — 20 kg",
            Quantity = 1234.567m,
            PriceAfterVat = 98765.43m,
            LineTotal = 100000000m,
            GrossTotal = 121928301.38m
        });
        invoice.Lines!.Add(new InvoiceLineDto { ItemCode = "NO-NAME", Quantity = 1m, LineTotal = 1m });
        invoice.FiscalVerificationCode = "60A74CD96120237760A74CD96120237760A74CD9";
        invoice.FiscalDeviceId = "DEVICE-SERIAL-0123456789-ABCDEFGHIJ";

        var slip = InvoiceSlipLayout.Compose(invoice, QrPayload);
        var lines = slip.Body.Concat(slip.Footer).ToList();

        Assert.All(lines, line => Assert.True(line.Text.Length <= InvoiceSlipLayout.Width, $"'{line.Text}' is {line.Text.Length} wide"));
        Assert.All(lines, line => Assert.All(line.Text, character => Assert.InRange(character, ' ', '~')));

        var text = string.Join('\n', lines.Select(line => line.Text));
        Assert.Contains("\"Mai Tatenda's\" Cafe", text);
        // A figure too wide to sit beside its label drops whole to its own line, still at the right edge.
        Assert.Contains(lines, line => line.Text == "121928301.38".PadLeft(InvoiceSlipLayout.Width));
        // No price for one: the quantity alone, and the item's code where it has no name.
        Assert.Contains(lines, line => line.Text == "NO-NAME");
        Assert.Contains(lines, line => line.Text.StartsWith("  Qty 1 ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cash_sale_with_no_buyer_and_no_QR_prints_neither()
    {
        var invoice = VanInvoice();
        invoice.CardName = null;
        invoice.BillToAddress = null;
        invoice.CustomerVatNo = null;
        invoice.VanSaleOrderNumber = null;

        var slip = InvoiceSlipLayout.Compose(invoice, "  ");
        var text = slip.Body.Concat(slip.Footer).Select(line => line.Text).ToList();

        Assert.Null(slip.QrPayload);
        Assert.DoesNotContain("Buyer", text);
        Assert.DoesNotContain(text, line => line.Contains("Sale ref", StringComparison.Ordinal));
        Assert.DoesNotContain(text, line => line.Contains("Scan", StringComparison.Ordinal));
        Assert.Contains(text, line => line.Contains("60A7-4CD9-6120-2377", StringComparison.Ordinal));
    }

    [Fact]
    public void The_PDF_is_one_narrow_page_carrying_the_slip_and_a_QR_that_can_be_tapped()
    {
        var bytes = Render(VanInvoice(), QrPayload, "slip");

        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
        Assert.Equal(1, pdf.GetNumberOfPages());

        var page = pdf.GetPage(1);
        var size = page.GetPageSize();
        // 58 mm roll proportions: far narrower than A4's 595 pt, and as tall as the slip needs.
        Assert.InRange(size.GetWidth(), 180f, 230f);
        Assert.True(size.GetHeight() > size.GetWidth() * 2, $"{size.GetWidth()} x {size.GetHeight()}");

        var text = PdfTextExtractor.GetTextFromPage(page);
        Assert.Contains("KEFALOS CHEESE PRODUCTS", text);
        Assert.Contains("FISCAL TAX INVOICE", text);
        Assert.Matches(@"Invoice\s+789106", text);
        Assert.Contains("Mbare Tuck Shop", text);
        Assert.Matches(@"2 x 7\.22\s+14\.44", text);
        Assert.Matches(@"TOTAL USD\s+23\.68", text);
        Assert.Contains("60A7-4CD9-6120-2377", text);
        Assert.Matches(@"Fiscal day\s+128", text);
        Assert.Contains("Thank you for your business!", text);

        var link = Assert.Single(page.GetAnnotations().OfType<PdfLinkAnnotation>());
        Assert.Equal(QrPayload, link.GetAction().GetAsString(PdfName.URI).ToUnicodeString());
    }

    [Fact]
    public void A_long_sale_is_still_one_page()
    {
        var invoice = VanInvoice();
        invoice.Lines = Enumerable.Range(1, 60)
            .Select(index => new InvoiceLineDto
            {
                ItemCode = $"ITM{index:000}",
                ItemDescription = $"Product number {index}",
                Quantity = index,
                PriceAfterVat = 1.15m,
                LineTotal = index,
                GrossTotal = index * 1.15m
            })
            .ToList();

        var bytes = Render(invoice, QrPayload, "slip-long");

        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
        Assert.Equal(1, pdf.GetNumberOfPages());
        var text = PdfTextExtractor.GetTextFromPage(pdf.GetPage(1));
        Assert.Contains("Product number 1", text);
        Assert.Contains("Product number 60", text);
        Assert.Contains("Thank you for your business!", text);
    }

    private static byte[] Render(InvoiceDto invoice, string? qrPayload, string name)
    {
        var bytes = Service.GenerateSlipPdf(invoice, qrPayload);

        var outputDirectory = Environment.GetEnvironmentVariable("INVOICE_PDF_OUT");
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllBytes(Path.Combine(outputDirectory, name + ".pdf"), bytes);
        }

        return bytes;
    }

    /// <summary>The van app's own slip fixture: two lines at $14.44 and $9.24, $23.68 in all.</summary>
    private static InvoiceDto VanInvoice() => new()
    {
        DocEntry = 2401234,
        DocNum = 789106,
        DocDate = "2026-10-11",
        CardCode = "VAN008",
        CardName = "Mbare Tuck Shop",
        BillToAddress = "Stand 12, Mbare\r\nHarare",
        CustomerVatNo = "220000041",
        VanSaleOrderNumber = "VO-20261011-0007",
        DocCurrency = "USD",
        DocTotal = 23.68m,
        VatSum = 3.09m,
        FiscalVerificationCode = "60A74CD961202377",
        FiscalDay = "128",
        FiscalDeviceId = "22862",
        FiscalReceiptGlobalNo = 21687,
        Lines =
        [
            new InvoiceLineDto
            {
                ItemCode = "SUP001", ItemDescription = "Superior White Cheddar 1kg", Quantity = 2m,
                UnitPrice = 6.28m, PriceAfterVat = 7.22m, LineTotal = 12.56m, GrossTotal = 14.44m
            },
            new InvoiceLineDto
            {
                ItemCode = "MLK500", ItemDescription = "Full Cream Milk 500ml", Quantity = 3m,
                UnitPrice = 2.68m, PriceAfterVat = 3.08m, LineTotal = 8.03m, GrossTotal = 9.24m
            }
        ]
    };
}
