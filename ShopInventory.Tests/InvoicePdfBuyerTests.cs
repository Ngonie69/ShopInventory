using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Features.Invoices;
using ShopInventory.Models;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// A van sale's invoice, rendered for real: the shop that bought is the customer on the page, and
/// nothing of the van's own card — its name, VAT number or TIN — is printed or even read.
/// </summary>
public sealed class InvoicePdfBuyerTests : IDisposable
{
    private readonly CustomerDocumentTestKit _kit = new();
    private int _businessPartnerReads;

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task The_shop_is_printed_as_the_customer_and_the_vans_card_is_not_read()
    {
        var text = await RenderAsync(new InvoicePdfBuyer("Mbare Tuck Shop", "Stand 12, Mbare", "220000041", "0772000041", null));

        Assert.Contains("Mbare Tuck Shop", text);
        Assert.Contains("Stand 12, Mbare", text);
        Assert.Contains("220000041", text);
        Assert.DoesNotContain("Van Sales West 2", text);
        Assert.DoesNotContain("VANVAT-008", text);
        Assert.DoesNotContain("VANTIN-008", text);
        Assert.Equal(0, _businessPartnerReads);
    }

    [Fact]
    public async Task Without_a_buyer_the_card_is_printed_as_every_download_prints_it()
    {
        var text = await RenderAsync(buyer: null);

        Assert.Contains("Van Sales West 2", text);
        Assert.Contains("VANVAT-008", text);
        Assert.Equal(1, _businessPartnerReads);
    }

    [Fact]
    public async Task Asked_for_the_receipt_form_the_same_invoice_and_buyer_come_out_as_the_till_slip()
    {
        var text = await RenderAsync(
            new InvoicePdfBuyer("Mbare Tuck Shop", "Stand 12, Mbare", "220000041", "0772000041", null),
            ReceiptPrintForm.Receipt48);

        Assert.Contains("KEFALOS CHEESE PRODUCTS", text);
        Assert.Contains("Mbare Tuck Shop", text);
        Assert.Matches(@"TOTAL USD\s+48\.30", text);
        Assert.DoesNotContain("Van Sales West 2", text);
        // The A4 sheet's own furniture is not on a slip.
        Assert.DoesNotContain("Please deposit into", text);
        Assert.Equal(0, _businessPartnerReads);
    }

    private async Task<string> RenderAsync(InvoicePdfBuyer? buyer, ReceiptPrintForm printForm = ReceiptPrintForm.InvoiceA4)
    {
        var sap = SapAnswers.Create(new()
        {
            ["GetInvoiceByDocEntryAsync"] = _ => Task.FromResult<Invoice?>(new Invoice
            {
                DocEntry = 2400100,
                DocNum = 780100,
                DocDate = "2026-10-09T00:00:00Z",
                CardCode = "VAN008",
                CardName = "Van Sales West 2",
                DocTotal = 48.30m,
                DocCurrency = "USD",
                Cancelled = "tNO",
                CancelStatus = "csNo"
            }),
            ["GetBusinessPartnerByCodeAsync"] = _ =>
            {
                _businessPartnerReads++;
                return Task.FromResult<ShopInventory.DTOs.BusinessPartnerDto?>(new ShopInventory.DTOs.BusinessPartnerDto
                {
                    CardCode = "VAN008",
                    CardName = "Van Sales West 2",
                    VatRegNo = "VANVAT-008",
                    TinNumber = "VANTIN-008"
                });
            }
        });

        await using var context = _kit.NewContext();
        var composer = new InvoicePdfComposer(
            context,
            sap,
            new NoReceipts(),
            new InvoicePdfService(NullLogger<InvoicePdfService>.Instance),
            new ShopInventory.Features.Invoices.Slip.InvoiceSlipPdfService(
                NullLogger<ShopInventory.Features.Invoices.Slip.InvoiceSlipPdfService>.Instance),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<InvoicePdfComposer>.Instance);

        var composed = await composer.ComposeAsync(2400100, null, null, CancellationToken.None, buyer, printForm);
        Assert.False(composed.IsError, composed.IsError ? composed.FirstError.Description : null);

        using var pdf = new PdfDocument(new PdfReader(new MemoryStream(composed.Value.PdfBytes)));
        return string.Join("\n", Enumerable.Range(1, pdf.GetNumberOfPages())
            .Select(page => PdfTextExtractor.GetTextFromPage(pdf.GetPage(page))));
    }

    private sealed class NoReceipts : IFiscalReceiptReader
    {
        public Task<FiscalReceiptSnapshot?> TryLookupAsync(int docNum, ReceiptType receiptType, ILogger logger, CancellationToken cancellationToken)
            => Task.FromResult<FiscalReceiptSnapshot?>(null);
    }
}
