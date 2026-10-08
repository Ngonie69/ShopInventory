using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Features.CustomerDocuments.Documents;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// The receipt printed on a customer's copy must be that invoice's own. A DocNum no longer proves it:
/// SAP reissued DocNums after the September 2026 update, and local rows still hold the old numbers,
/// which now name other customers' invoices.
/// </summary>
public sealed class FiscalLinkVerifierTests : IDisposable
{
    private const int DocNum = 780100;
    private static readonly DateTime InvoiceDay = DateTime.UtcNow.Date.AddDays(-2);

    private readonly CustomerDocumentTestKit _kit = new();
    private readonly FakeReceiptReader _device = new();

    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task A_sale_found_by_its_reference_vouches_for_its_invoice()
    {
        await AddSaleAsync("KEF-FAC-1", sapDocNum: DocNum);

        var verdict = await VerifyAsync(Query(saleReference: "KEF-FAC-1"));

        Assert.Equal(FiscalLinkVerdictKind.Verified, verdict.Kind);
        Assert.Equal(FiscalLinkVerifier.SaleReferenceSource, verdict.Source);
        Assert.Equal("QR-SALE", verdict.Receipt!.QrCode);
        Assert.Equal("36189", verdict.Receipt.DeviceId);
    }

    [Fact]
    public async Task A_sale_that_records_a_different_invoice_is_a_mismatch()
    {
        await AddSaleAsync("KEF-FAC-1", sapDocNum: 779999);

        var verdict = await VerifyAsync(Query(saleReference: "KEF-FAC-1"));

        Assert.Equal(FiscalLinkVerdictKind.Mismatch, verdict.Kind);
        Assert.Contains("779999", verdict.Reason);
    }

    [Fact]
    public async Task A_logged_receipt_for_the_same_customer_and_total_is_verified()
    {
        await AddTransactionAsync(cardCode: CustomerDocumentTestKit.CardCode, total: 125.50m);

        var verdict = await VerifyAsync(Query());

        Assert.Equal(FiscalLinkVerdictKind.Verified, verdict.Kind);
        Assert.Equal(FiscalLinkVerifier.TransactionLogSource, verdict.Source);
        Assert.Equal("QR-LOG", verdict.Receipt!.QrCode);
    }

    [Theory]
    [InlineData("CHE012", 125.50, 0, false, "customer CHE012")]
    [InlineData(CustomerDocumentTestKit.CardCode, 23347.75, 0, false, "total of 23,347.75")]
    [InlineData(CustomerDocumentTestKit.CardCode, 125.50, -10, false, "before the invoice existed")]
    [InlineData(CustomerDocumentTestKit.CardCode, 125.50, 0, true, "before the SAP update")]
    public async Task A_logged_receipt_that_belongs_to_another_document_is_a_mismatch(
        string cardCode, double total, int daysFromInvoice, bool reposted, string reason)
    {
        await AddTransactionAsync(cardCode, (decimal)total, InvoiceDay.AddDays(daysFromInvoice).AddHours(10), reposted);

        var verdict = await VerifyAsync(Query());

        Assert.Equal(FiscalLinkVerdictKind.Mismatch, verdict.Kind);
        Assert.Contains(reason, verdict.Reason);
    }

    [Fact]
    public async Task A_foreign_currency_receipt_matches_the_documents_own_total()
    {
        await AddTransactionAsync(cardCode: CustomerDocumentTestKit.CardCode, total: 3420.00m);

        var verdict = await VerifyAsync(Query(docTotal: 125.50m, docTotalFc: 3420.00m));

        Assert.Equal(FiscalLinkVerdictKind.Verified, verdict.Kind);
    }

    [Fact]
    public async Task With_nothing_recorded_it_waits_until_the_device_may_be_asked()
    {
        var verdict = await VerifyAsync(Query(), allowDevice: false);

        Assert.Equal(FiscalLinkVerdictKind.NotYet, verdict.Kind);
        Assert.Equal(0, _device.Calls);
    }

    [Fact]
    public async Task The_device_vouches_for_a_receipt_signed_after_the_invoice()
    {
        _device.Answer = Snapshot(InvoiceDay.AddHours(9));

        var verdict = await VerifyAsync(Query(), allowDevice: true);

        Assert.Equal(FiscalLinkVerdictKind.Verified, verdict.Kind);
        Assert.Equal(FiscalLinkVerifier.DeviceSource, verdict.Source);
    }

    [Fact]
    public async Task A_device_receipt_signed_before_the_invoice_is_a_mismatch()
    {
        _device.Answer = Snapshot(InvoiceDay.AddDays(-30));

        var verdict = await VerifyAsync(Query(), allowDevice: true);

        Assert.Equal(FiscalLinkVerdictKind.Mismatch, verdict.Kind);
    }

    [Fact]
    public async Task A_device_that_cannot_be_asked_means_wait_not_unfiscalised()
    {
        _device.Answer = null;

        var verdict = await VerifyAsync(Query(), allowDevice: true);

        Assert.Equal(FiscalLinkVerdictKind.NotYet, verdict.Kind);
        Assert.Contains("could not be asked", verdict.Reason);
    }

    private static FiscalLinkQuery Query(string? saleReference = null, decimal? docTotal = 125.50m, decimal? docTotalFc = null) =>
        new(DocNum, CustomerDocumentTestKit.CardCode, docTotal, docTotalFc, InvoiceDay, saleReference, DateTime.UtcNow.AddMinutes(-1));

    private async Task<FiscalLinkVerdict> VerifyAsync(FiscalLinkQuery query, bool allowDevice = false)
    {
        await using var context = _kit.NewContext();
        return await FiscalLinkVerifier.VerifyAsync(context, _device, query, allowDevice, NullLogger.Instance, CancellationToken.None);
    }

    private async Task AddSaleAsync(string reference, int? sapDocNum)
    {
        await using var context = _kit.NewContext();
        context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            CardCode = "CIS006",
            WarehouseCode = "FAC",
            DocDate = InvoiceDay,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            FiscalQRCode = "QR-SALE",
            FiscalVerificationCode = "SALE-CODE",
            FiscalDayNo = "81",
            FiscalDeviceId = 36189,
            ReceiptGlobalNo = 4410,
            SapDocNum = sapDocNum
        });
        await context.SaveChangesAsync();
    }

    private async Task AddTransactionAsync(string cardCode, decimal total, DateTime? signedAtUtc = null, bool reposted = false)
    {
        await using var context = _kit.NewContext();
        context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = Guid.NewGuid().ToString(),
            DocumentType = "Invoice",
            DocNum = DocNum,
            Status = "Success",
            QRCode = "QR-LOG",
            VerificationCode = "LOG-CODE",
            CardCode = cardCode,
            DocTotal = total,
            TimestampUtc = signedAtUtc ?? InvoiceDay.AddHours(10),
            RepostedAfterSapUpdate = reposted
        });
        await context.SaveChangesAsync();
    }

    private static FiscalReceiptSnapshot Snapshot(DateTime signedAtUtc) => new(
        IsFiscalised: true,
        ReceiptGlobalNo: 77,
        QrCode: "QR-DEVICE",
        VerificationCode: "DEV-CODE",
        DeviceSerialNumber: "SIS2026",
        DeviceId: "36189",
        FiscalDay: "81",
        TimestampUtc: signedAtUtc,
        RawResponseJson: null);

    private sealed class FakeReceiptReader : IFiscalReceiptReader
    {
        public FiscalReceiptSnapshot? Answer { get; set; }
        public int Calls { get; private set; }

        public Task<FiscalReceiptSnapshot?> TryLookupAsync(int docNum, ReceiptType receiptType, ILogger logger, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Answer);
        }
    }
}
