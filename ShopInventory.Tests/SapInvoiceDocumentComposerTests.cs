using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task A_van_sale_not_yet_in_SAP_waits_and_reads_nothing()
    {
        var saleId = await AddVanSaleAsync(sapDocEntry: null, sapDocNum: null);

        var composition = await ComposeAsync(VanRow(saleId));

        Assert.Equal(DocumentCompositionKind.WaitForFiscal, composition.Kind);
        Assert.Contains("reach SAP", composition.Reason);
        Assert.Equal(0, _pdf.Calls);
    }

    [Fact]
    public async Task A_van_sale_once_posted_is_sent_as_its_invoice_with_the_shop_as_buyer()
    {
        var saleId = await AddVanSaleAsync(sapDocEntry: 2400100, sapDocNum: 780100);
        await AddReceiptAsync(cardCode: "VAN008");
        _pdf.Invoice = invoice =>
        {
            invoice.CardCode = "VAN008";
            invoice.CardName = "Van Sales West 2";
        };
        CustomerDocumentDeliveryEntity? row = null;

        var composition = await ComposeAsync(delivery =>
        {
            VanRow(saleId)(delivery);
            row = delivery;
        });

        Assert.Equal(DocumentCompositionKind.Ready, composition.Kind);
        Assert.Equal(2400100, row!.SapDocEntry);
        Assert.Equal(780100, row.SapDocNum);
        Assert.Equal("780100", row.DocumentNumber);
        Assert.Equal(new InvoicePdfBuyer("Mbare Tuck Shop", "Stand 12, Mbare", "220000041", "0772000041", null), _pdf.Buyer);
        Assert.Contains("Mbare Tuck Shop", composition.Document!.Caption);
        Assert.DoesNotContain("Van Sales West 2", composition.Document.Caption);
        Assert.Equal("Kefalos-Invoice-780100.pdf", composition.Document.FileName);
    }

    [Fact]
    public async Task An_account_customers_invoice_prints_its_card_as_before()
    {
        await AddReceiptAsync();

        await ComposeAsync();

        Assert.Null(_pdf.Buyer);
    }

    [Fact]
    public async Task A_van_sale_that_now_bills_another_card_is_held()
    {
        var saleId = await AddVanSaleAsync(sapDocEntry: 2400100, sapDocNum: 780100, cardCode: "VAN009");

        var composition = await ComposeAsync(VanRow(saleId));

        Assert.Equal(DocumentCompositionKind.Hold, composition.Kind);
        Assert.Equal(0, _pdf.Calls);
    }

    private static Action<CustomerDocumentDeliveryEntity> VanRow(int saleId) => row =>
    {
        row.SapDocEntry = null;
        row.SapDocNum = null;
        row.DesktopSaleId = saleId;
        row.DocumentNumber = $"INV{saleId}";
        row.CardCode = "VAN008";
        row.CardName = "Mbare Tuck Shop";
        row.RouteCustomerId = 41;
        row.RouteCustomerName = "Mbare Tuck Shop";
        row.SaleReference = "VO-20261009-0042";
        row.Trigger = CustomerDocumentDeliveryTrigger.Counter;
    };

    private async Task<int> AddVanSaleAsync(int? sapDocEntry, int? sapDocNum, string cardCode = "VAN008")
    {
        await using var context = _kit.NewContext();
        if (!await context.RouteCustomers.AnyAsync(customer => customer.Id == 41))
        {
            context.RouteCustomers.Add(new RouteCustomerEntity
            {
                Id = 41, AssignedBusinessPartnerCode = "VAN008", Code = "S41", Name = "Mbare Tuck Shop",
                Address = "Stand 12, Mbare", VatNumber = "220000041", Phone = "0772000041"
            });
        }

        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = "VO-20261009-0042",
            SourceSystem = "KefalosVanSalesOnline",
            CardCode = cardCode,
            RouteCustomerId = 41,
            RouteCustomerName = "Mbare Tuck Shop",
            DocDate = DateTime.UtcNow.Date,
            TotalAmount = 125.50m,
            Currency = "USD",
            WarehouseCode = "VAN004",
            SapDocEntry = sapDocEntry,
            SapDocNum = sapDocNum
        };
        context.DesktopSales.Add(sale);
        await context.SaveChangesAsync();
        return sale.Id;
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

    private async Task AddReceiptAsync(decimal total = 125.50m, string cardCode = CustomerDocumentTestKit.CardCode)
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
            CardCode = cardCode,
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

        public InvoicePdfBuyer? Buyer { get; private set; }

        public Task<ErrorOr<ComposedInvoicePdf>> ComposeAsync(int docEntry, string? requestedQrCode, InvoicePdfReceipt? verifiedReceipt, CancellationToken cancellationToken, InvoicePdfBuyer? buyer = null)
        {
            Calls++;
            Receipt = verifiedReceipt;
            Buyer = buyer;

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
