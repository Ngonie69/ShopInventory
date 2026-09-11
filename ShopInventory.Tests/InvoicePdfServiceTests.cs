using System.Text.RegularExpressions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Reads the Fiscal Tax Invoice PDF back as text, page by page.
/// </summary>
/// <remarks>
/// A layout is proven by looking at it. These pin what a look at one page can miss: the page count
/// stamped on every sheet agreeing with the sheets there are, the footer printed once and only on the
/// last, no line lost or repeated across a page break, and the VAT column carrying each line's own
/// VAT rather than a share of the document's.
///
/// Set <c>INVOICE_PDF_OUT</c> to a directory to keep every rendered PDF for inspection.
/// </remarks>
public class InvoicePdfServiceTests
{
    private const string QrPayload = "https://fdms.zimra.co.zw/000002286211092026000021687760A74CD961202377";

    private static readonly InvoicePdfService Service = new(NullLogger<InvoicePdfService>.Instance);

    [Fact]
    public async Task TheDesignsInvoiceFitsOnOnePageWithItsFiscalBlock()
    {
        var page = Assert.Single(await RenderAsync(DesignInvoice(), "design"));

        Assert.Matches(@"PAGE\s+1\s+OF\s+1", page);
        Assert.Matches(@"DOC NUMBER\s+772398", page);
        Assert.Matches(@"INVOICE NUMBER\s+772398", page);
        // Extraction reads the page line by line across its width, so the bank details sharing the
        // label's line come between the label and a code that has dropped below it. What matters is
        // that the code prints whole, never broken at a hyphen.
        Assert.Contains("Verification Code:", page);
        Assert.Contains("60A7-4CD9-6120-2377", page);
        Assert.Matches(@"Fiscal Day:\s+128", page);
        Assert.Matches(@"Device ID:\s+22862", page);
        Assert.Contains("https://fdms.zimra.co.zw", page);
        Assert.Contains("9140005966435", page);
        Assert.DoesNotContain("COPY", page);
    }

    [Fact]
    public async Task AnUnfiscalisedInvoicePrintsNoFiscalBlock()
    {
        var invoice = DesignInvoice();
        invoice.FiscalVerificationCode = null;
        invoice.FiscalDay = null;
        invoice.FiscalDeviceId = null;

        var page = Assert.Single(await RenderAsync(invoice, "unfiscalised", fiscalQrCode: null));

        Assert.DoesNotContain("Verification Code", page);
        Assert.DoesNotContain("fdms.zimra.co.zw", page);
        Assert.Contains("Please deposit into:", page);
        Assert.Contains("Invoice Total", page);
    }

    [Fact]
    public async Task EachLinePrintsItsOwnVat()
    {
        // One standard-rated line and one zero-rated. Spread by value, the document's 1.55 VAT would
        // have printed 1.41 on the cheese and 0.14 on the zero-rated milk.
        var invoice = DesignInvoice();
        invoice.Lines =
        [
            Line("CHE001", "Cheddar", 1m, net: 10.00m, gross: 11.55m),
            Line("MLK001", "Milk", 1m, net: 1.00m, gross: 1.00m),
        ];
        invoice.VatSum = 1.55m;
        invoice.DocTotal = 12.55m;

        var page = Assert.Single(await RenderAsync(invoice, "line-vat"));

        Assert.Matches(@"CHE001\s+1\s+Cheddar\s+10\.00\s+10\.00\s+1\.55\s+11\.55", page);
        Assert.Matches(@"MLK001\s+1\s+Milk\s+1\.00\s+1\.00\s+0\.00\s+1\.00", page);
    }

    [Fact]
    public async Task AZeroRatedInvoiceIsNotChargedAGuessedRate()
    {
        var invoice = DesignInvoice();
        invoice.Lines = [Line("MLK001", "Milk", 2m, net: 4.00m, gross: 4.00m)];
        invoice.VatSum = 0m;
        invoice.DocTotal = 4.00m;

        var page = Assert.Single(await RenderAsync(invoice, "zero-rated"));

        Assert.Matches(@"MLK001\s+2\s+Milk\s+2\.00\s+4\.00\s+0\.00\s+4\.00", page);
    }

    [Fact]
    public async Task EverythingTheDesignLeavesOutStillPrints()
    {
        var invoice = DesignInvoice();
        invoice.BillToAddress = "Stand 4471, Corner Samora Machel Avenue and Julius Nyerere Way\nHarare CBD\nHarare";
        invoice.ShipToAddress = "Warehouse 9, Graniteside Industrial Park\nHarare";
        invoice.CustomerVatNo = "220987654";
        invoice.CustomerTinNumber = "2000123456";
        invoice.CustomerPhone = "+263 77 123 4567";
        invoice.CustomerEmail = "accounts@example.co.zw";
        invoice.Lines =
        [
            Line("SUP001", "Superior White Cheddar Mature Block, vacuum sealed, catering pack of four 2.5 kg units", 12m, net: 480.00m, gross: 554.40m),
            Line("MLK001", "Fresh Milk 2L", 30m, net: 60.00m, gross: 60.00m),
        ];
        invoice.Lines[0].DiscountPercent = 5m;
        invoice.VatSum = 74.40m;
        invoice.DocTotal = 614.40m;

        var page = Assert.Single(await RenderAsync(invoice, "everything"));

        Assert.Contains("Graniteside", page);
        Assert.Contains("accounts@example.co.zw", page);
        Assert.Matches(@"TIN NUMBER:\s+2000123456", page);
    }

    public static TheoryData<int> LineCounts()
    {
        var counts = new TheoryData<int>();
        foreach (var count in Enumerable.Range(1, 70))
        {
            counts.Add(count);
        }

        return counts;
    }

    [Theory]
    [MemberData(nameof(LineCounts))]
    public async Task ALongInvoiceCountsItsPagesAndEndsOnTheFooter(int lineCount)
    {
        var invoice = DesignInvoice();
        invoice.Lines = Enumerable.Range(1, lineCount)
            .Select(index => Line($"ITM{index:D3}", $"Item number {index}", 1m, net: 1.00m, gross: 1.15m))
            .ToList();
        invoice.VatSum = 0.15m * lineCount;
        invoice.DocTotal = 1.15m * lineCount;

        var pages = await RenderAsync(invoice, $"lines-{lineCount:D2}");

        for (var index = 0; index < pages.Count; index++)
        {
            Assert.Matches($@"PAGE\s+{index + 1}\s+OF\s+{pages.Count}", pages[index]);
        }

        Assert.Single(pages.SelectMany(page => Regex.Matches(page, "Invoice Total")));
        Assert.Contains("Invoice Total", pages[^1]);

        var printedCodes = pages
            .SelectMany(page => Regex.Matches(page, @"ITM\d{3}").Select(match => match.Value))
            .ToList();
        Assert.Equal(invoice.Lines.Select(line => line.ItemCode!), printedCodes);
    }

    private static async Task<List<string>> RenderAsync(
        InvoiceDto invoice,
        string name,
        string? fiscalQrCode = QrPayload)
    {
        var bytes = await Service.GenerateInvoicePdfAsync(invoice, fiscalQrCode);

        var outputDirectory = Environment.GetEnvironmentVariable("INVOICE_PDF_OUT");
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, name + ".pdf"), bytes);
        }

        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(bytes)));
        return Enumerable.Range(1, pdf.GetNumberOfPages())
            .Select(pageNumber => PdfTextExtractor.GetTextFromPage(pdf.GetPage(pageNumber)))
            .ToList();
    }

    private static InvoiceDto DesignInvoice() => new()
    {
        DocEntry = 772398,
        DocNum = 772398,
        DocDate = "2026-09-11",
        CardCode = "KEF001",
        CardName = "Kefalos Shop",
        DocCurrency = "USD",
        DocTotal = 1.15m,
        VatSum = 0.15m,
        FiscalVerificationCode = "60A74CD961202377",
        FiscalDay = "128",
        FiscalDeviceId = "22862",
        Lines = [Line("SUP001", "Superior White", 1m, net: 1.00m, gross: 1.15m)],
    };

    private static InvoiceLineDto Line(string itemCode, string description, decimal quantity, decimal net, decimal gross) => new()
    {
        ItemCode = itemCode,
        ItemDescription = description,
        Quantity = quantity,
        UnitPrice = net / quantity,
        LineTotal = net,
        GrossTotal = gross,
    };
}
