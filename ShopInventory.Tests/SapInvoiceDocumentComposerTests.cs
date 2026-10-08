using ErrorOr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Documents;
using ShopInventory.Features.Invoices;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Preparing a SAP invoice for a customer: verified receipt first, then the PDF — and the last checks
/// that the invoice should still go, made on the document as SAP holds it at that moment.
/// </summary>
public sealed class SapInvoiceDocumentComposerTests : IDisposable
{
    private readonly CustomerDocumentTestKit _kit = new();
    private readonly FakePdfComposer _pdf = new();
    private readonly NoDevice _device = new();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task Without_a_receipt_nothing_is_read_from_SAP()
    {
        var composition = await ComposeAsync();

        Assert.Equal(DocumentCompositionKind.WaitForFiscal, composition.Kind);
        Assert.Equal(0, _pdf.Calls);
    }

    [Fact]
    public async Task A_verified_receipt_is_printed_and_the_document_named_and_captioned()
    {
        await AddReceiptAsync();

        var composition = await ComposeAsync();

        Assert.Equal(DocumentCompositionKind.Ready, composition.Kind);
        Assert.Equal("QR-LOG", _pdf.Receipt!.QrCode);
        var document = composition.Document!;
        Assert.Equal("Kefalos-Invoice-780100.pdf", document.FileName);
        Assert.Contains("Spar Bridge", document.Caption);
        Assert.Contains("USD 125.50", document.Caption);
        Assert.Equal(64, document.Sha256.Length);
    }

    [Fact]
    public async Task A_foreign_currency_invoice_is_captioned_in_its_own_currency()
    {
        await AddReceiptAsync(total: 3420.00m);
        _pdf.Invoice = invoice =>
        {
            invoice.DocCurrency = "ZiG";
            invoice.DocTotalFc = 3420.00m;
        };

        var composition = await ComposeAsync(delivery => delivery.DocumentTotalFc = 3420.00m);

        Assert.Contains("ZiG 3,420.00", composition.Document!.Caption);
    }

    [Fact]
    public async Task An_invoice_cancelled_while_it_waited_is_withdrawn()
    {
        await AddReceiptAsync();
        _pdf.Invoice = invoice => invoice.CancelStatus = "csYes";

        var composition = await ComposeAsync();

        Assert.Equal(DocumentCompositionKind.Cancel, composition.Kind);
    }

    [Fact]
    public async Task A_DocEntry_that_now_names_another_document_is_held()
    {
        await AddReceiptAsync();
        _pdf.Invoice = invoice =>
        {
            invoice.DocNum = 779341;
            invoice.CardCode = "CHE012";
        };

        var composition = await ComposeAsync();

        Assert.Equal(DocumentCompositionKind.Hold, composition.Kind);
        Assert.Contains("CHE012", composition.Reason);
    }

    [Fact]
    public async Task A_document_larger_than_allowed_fails()
    {
        await AddReceiptAsync();
        _pdf.Bytes = new byte[2048];

        var composition = await ComposeAsync(settings: change => change.MaxDocumentBytes = 1024);

        Assert.Equal(DocumentCompositionKind.Fail, composition.Kind);
    }

    [Fact]
    public async Task SAP_not_answering_is_tried_again_and_an_invoice_gone_from_SAP_fails()
    {
        await AddReceiptAsync();

        _pdf.Error = Error.Failure("Invoice.SapTimeout", "Connection to SAP Service Layer timed out.");
        var unreachable = await ComposeAsync();

        _pdf.Error = Error.NotFound("Invoice.NotFound", "gone");
        var gone = await ComposeAsync();

        Assert.Equal(DocumentCompositionKind.Retry, unreachable.Kind);
        Assert.Equal(DocumentCompositionKind.Fail, gone.Kind);
    }

    private async Task<DocumentComposition> ComposeAsync(
        Action<CustomerDocumentDeliveryEntity>? delivery = null,
        Action<CustomerDocumentDeliverySettings>? settings = null)
    {
        var row = new CustomerDocumentDeliveryEntity
        {
            DocumentType = CustomerDocumentType.SapInvoice,
            SapDocEntry = 2400100,
            SapDocNum = 780100,
            DocumentNumber = "780100",
            DocumentDate = DateTime.UtcNow.Date,
            DocumentTotal = 125.50m,
            CardCode = CustomerDocumentTestKit.CardCode,
            CardName = "Spar Bridge",
            RecipientE164 = "+263771234567",
            CreatedAtUtc = DateTime.UtcNow
        };
        delivery?.Invoke(row);

        await using var context = _kit.NewContext();
        var composer = new SapInvoiceDocumentComposer(
            context,
            _device,
            _pdf,
            Options.Create(CustomerDocumentTestKit.Settings(settings)),
            Options.Create(new FiscalisationSettings()),
            NullLogger<SapInvoiceDocumentComposer>.Instance);

        return await composer.ComposeAsync(row, DateTime.UtcNow, CancellationToken.None);
    }

    private async Task AddReceiptAsync(decimal total = 125.50m)
    {
        await using var context = _kit.NewContext();
        context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = Guid.NewGuid().ToString(),
            DocumentType = "Invoice",
            DocNum = 780100,
            Status = "Success",
            QRCode = "QR-LOG",
            VerificationCode = "LOG-CODE",
            CardCode = CustomerDocumentTestKit.CardCode,
            DocTotal = total,
            TimestampUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private sealed class FakePdfComposer : IInvoicePdfComposer
    {
        public int Calls { get; private set; }
        public InvoicePdfReceipt? Receipt { get; private set; }
        public Action<Invoice>? Invoice { get; set; }
        public byte[] Bytes { get; set; } = [0x25, 0x50, 0x44, 0x46];
        public Error? Error { get; set; }

        public Task<ErrorOr<ComposedInvoicePdf>> ComposeAsync(int docEntry, string? requestedQrCode, InvoicePdfReceipt? verifiedReceipt, CancellationToken cancellationToken)
        {
            Calls++;
            Receipt = verifiedReceipt;

            if (Error is { } error)
                return Task.FromResult<ErrorOr<ComposedInvoicePdf>>(error);

            var sap = new Invoice
            {
                DocEntry = docEntry,
                DocNum = 780100,
                DocDate = DateTime.UtcNow.Date.ToString("yyyy-MM-dd"),
                CardCode = CustomerDocumentTestKit.CardCode,
                CardName = "Spar Bridge",
                DocTotal = 125.50m,
                DocCurrency = "USD",
                Cancelled = "tNO",
                CancelStatus = "csNo"
            };
            Invoice?.Invoke(sap);

            var dto = new InvoiceDto
            {
                DocEntry = sap.DocEntry,
                DocNum = sap.DocNum,
                DocDate = sap.DocDate,
                CardCode = sap.CardCode,
                CardName = sap.CardName,
                DocTotal = sap.DocTotal,
                DocCurrency = sap.DocCurrency
            };

            return Task.FromResult<ErrorOr<ComposedInvoicePdf>>(new ComposedInvoicePdf(Bytes, dto, sap, verifiedReceipt?.QrCode));
        }
    }

    private sealed class NoDevice : IFiscalReceiptReader
    {
        public Task<FiscalReceiptSnapshot?> TryLookupAsync(int docNum, ReceiptType receiptType, ILogger logger, CancellationToken cancellationToken)
            => Task.FromResult<FiscalReceiptSnapshot?>(null);
    }
}
