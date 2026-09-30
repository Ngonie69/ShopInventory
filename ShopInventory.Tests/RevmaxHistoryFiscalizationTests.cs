using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// Once the platform files everything, REVMax still holds what it filed before. Nothing it holds may be
/// filed a second time on the platform, and only REVMax can credit its own receipts.
/// </summary>
public sealed class RevmaxHistoryFiscalizationTests
{
    private const int OurRevmaxDevice = 22862;
    private static readonly DateTime LastFilingDate = new(2026, 9, 30);

    [Fact]
    public async Task An_invoice_REVMax_already_filed_is_adopted_and_not_filed_again()
    {
        var revmax = new FakeRevmax { ["771485"] = Held("FiscalInvoice", 216407) };
        var platform = new FakePlatform();

        var result = await Service(platform, revmax).FiscalizeInvoiceAsync(Invoice(771485, "2026-09-10"));

        Assert.True(result.Success);
        Assert.True(result.AlreadyFiscalised);
        Assert.Equal("216407", result.ReceiptGlobalNo);
        Assert.Empty(platform.Filed);
    }

    [Fact]
    public async Task An_old_invoice_REVMax_says_it_never_filed_goes_to_the_platform()
    {
        var revmax = new FakeRevmax();
        var platform = new FakePlatform();

        await Service(platform, revmax).FiscalizeInvoiceAsync(Invoice(771486, "2026-09-10"));

        Assert.Equal(["771486"], revmax.Asked);
        Assert.Single(platform.Filed);
    }

    [Fact]
    public async Task Another_devices_receipt_under_the_same_number_is_not_ours_to_adopt()
    {
        var revmax = new FakeRevmax { ["50000"] = Held("FiscalInvoice", 8406, deviceId: "3044") };
        var platform = new FakePlatform();

        var result = await Service(platform, revmax).FiscalizeInvoiceAsync(Invoice(50000, "2026-09-10"));

        Assert.False(result.AlreadyFiscalised);
        Assert.Single(platform.Filed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_old_invoice_is_held_back_when_REVMax_cannot_answer(bool unreachable)
    {
        var revmax = unreachable ? new FakeRevmax { Unreachable = true } : new FakeRevmax { Busy = true };
        var platform = new FakePlatform();

        var result = await Service(platform, revmax).FiscalizeInvoiceAsync(Invoice(771487, "2026-09-10"));

        Assert.False(result.Success);
        Assert.Equal(RevmaxHistoryFiscalizationService.HistoryUnavailableErrorCode, result.ErrorCode);
        Assert.Empty(platform.Filed);
    }

    [Fact]
    public async Task An_invoice_dated_after_REVMax_stopped_filing_never_waits_on_REVMax()
    {
        var revmax = new FakeRevmax { Unreachable = true };
        var platform = new FakePlatform();

        await Service(platform, revmax).FiscalizeInvoiceAsync(Invoice(780001, "2026-10-01"));

        Assert.Empty(revmax.Asked);
        Assert.Single(platform.Filed);
    }

    [Fact]
    public async Task With_no_last_filing_date_every_invoice_is_checked_against_REVMax()
    {
        var revmax = new FakeRevmax();
        var platform = new FakePlatform();

        await Service(platform, revmax, noLastFilingDate: true).FiscalizeInvoiceAsync(Invoice(790001, "2027-01-05"));

        Assert.Equal(["790001"], revmax.Asked);
        Assert.Single(platform.Filed);
    }

    [Fact]
    public async Task A_credit_against_a_platform_invoice_goes_to_the_platform_without_asking_REVMax()
    {
        var revmax = new FakeRevmax { Unreachable = true };
        var platform = new FakePlatform { Holds = { "780001" } };

        await Service(platform, revmax).FiscalizeCreditNoteAsync(Invoice(5001, "2026-10-02"), "780001");

        Assert.Empty(revmax.Asked);
        Assert.Single(platform.Filed);
        Assert.Empty(revmax.Posted);
    }

    [Fact]
    public async Task A_credit_against_a_REVMax_invoice_is_filed_on_REVMax()
    {
        var revmax = new FakeRevmax { ["771485"] = Held("FiscalInvoice", 216407) };
        var platform = new FakePlatform();

        var result = await Service(platform, revmax).FiscalizeCreditNoteAsync(Invoice(5002, "2026-10-02"), "771485");

        Assert.True(revmax.Posted.Count > 0, $"{result.ErrorCode}: {result.Message}");
        Assert.Empty(platform.Filed);
    }

    [Fact]
    public async Task A_credit_is_held_back_when_nobody_can_say_where_its_original_was_filed()
    {
        var revmax = new FakeRevmax { Unreachable = true };
        var platform = new FakePlatform();

        var result = await Service(platform, revmax).FiscalizeCreditNoteAsync(
            Invoice(5003, "2026-10-02"), "771485");

        Assert.Equal(RevmaxHistoryFiscalizationService.HistoryUnavailableErrorCode, result.ErrorCode);
        Assert.Empty(platform.Filed);
        Assert.Empty(revmax.Posted);
    }

    [Fact]
    public async Task A_till_sale_REVMax_signed_before_the_switch_is_found_on_retry()
    {
        var revmax = new FakeRevmax { ["SI-123456"] = Held("FiscalInvoice", 216500) };
        var platform = new FakePlatform();

        var found = await Service(platform, revmax).FindPreSapReceiptAsync("123456");

        Assert.NotNull(found);
        Assert.Equal("216500", found.ReceiptGlobalNo);
    }

    [Fact]
    public async Task A_till_sale_retry_throws_rather_than_sign_when_REVMax_cannot_be_asked()
    {
        var service = Service(new FakePlatform(), new FakeRevmax { Unreachable = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.FindPreSapReceiptAsync("123457"));
    }

    [Fact]
    public async Task The_status_read_takes_REVMax_receipt_when_the_platform_has_none()
    {
        var reader = Reader(new FakePlatform(), new FakeRevmax { ["771485"] = Held("FiscalInvoice", 216407) });

        var snapshot = await reader.TryLookupAsync(771485, ReceiptType.FiscalInvoice, NullLogger.Instance, default);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsFiscalised);
        Assert.Equal(216407, snapshot.ReceiptGlobalNo);
    }

    [Fact]
    public async Task The_status_read_says_not_fiscalised_only_when_both_say_so()
    {
        var reader = Reader(new FakePlatform(), new FakeRevmax());

        var snapshot = await reader.TryLookupAsync(771486, ReceiptType.FiscalInvoice, NullLogger.Instance, default);

        Assert.NotNull(snapshot);
        Assert.False(snapshot.IsFiscalised);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_status_read_records_nothing_when_REVMax_cannot_answer(bool unreachable)
    {
        var revmax = unreachable ? new FakeRevmax { Unreachable = true } : new FakeRevmax { Busy = true };
        var reader = Reader(new FakePlatform(), revmax);

        Assert.Null(await reader.TryLookupAsync(771487, ReceiptType.FiscalInvoice, NullLogger.Instance, default));
    }

    [Fact]
    public async Task The_status_read_does_not_ask_REVMax_about_a_platform_receipt()
    {
        var revmax = new FakeRevmax { Unreachable = true };
        var reader = Reader(new FakePlatform { Holds = { "780001" } }, revmax);

        var snapshot = await reader.TryLookupAsync(780001, ReceiptType.FiscalInvoice, NullLogger.Instance, default);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsFiscalised);
        Assert.Empty(revmax.Asked);
    }

    private static RevmaxHistoryFiscalizationService Service(
        FakePlatform platform, FakeRevmax revmax, bool noLastFilingDate = false)
    {
        var revmaxSettings = Options.Create(new RevmaxSettings
        {
            Enabled = true,
            DefaultRefDeviceId = OurRevmaxDevice,
            LastFilingDate = noLastFilingDate ? null : LastFilingDate
        });
        var fiscalisation = Options.Create(new FiscalisationSettings
        {
            Provider = FiscalisationProvider.Platform,
            DefaultTaxId = 515
        });

        return new RevmaxHistoryFiscalizationService(
            new FiscalizationService(
                platform.Client,
                StubProxy.For<IFiscalDeviceConfigCache>((_, _) => Task.FromResult<FiscalConfigApiResponse?>(null)),
                fiscalisation,
                NullLogger<FiscalizationService>.Instance),
            new RevmaxFiscalizationService(
                revmax.Client,
                revmaxSettings,
                Options.Create(new TaxSettings()),
                fiscalisation,
                NullLogger<RevmaxFiscalizationService>.Instance),
            revmaxSettings,
            NullLogger<RevmaxHistoryFiscalizationService>.Instance);
    }

    private static IFiscalReceiptReader Reader(FakePlatform platform, FakeRevmax revmax) =>
        new RevmaxHistoryFiscalReceiptReader(
            new PlatformFiscalReceiptReader(
                platform.Client,
                StubProxy.For<IFiscalDeviceConfigCache>((_, _) => Task.FromResult<FiscalConfigApiResponse?>(null))),
            new RevmaxFiscalReceiptReader(
                revmax.Client,
                new RevmaxSettings { Enabled = true, DefaultRefDeviceId = OurRevmaxDevice }));

    private static InvoiceDto Invoice(int docNum, string docDate) => new()
    {
        DocEntry = docNum + 1_000_000,
        DocNum = docNum,
        DocDate = docDate,
        DocCurrency = "USD",
        DocTotal = 10m,
        Lines = [new InvoiceLineDto { LineNum = 1, ItemCode = "A", Quantity = 1, UnitPrice = 10m, TaxCode = "O01" }]
    };

    private static InvoiceResponse Held(string receiptType, long globalNo, string deviceId = "22862") => new()
    {
        Code = "1",
        Message = "Success",
        DeviceID = deviceId,
        DeviceSerialNumber = "8DE6996C0188",
        QRcode = "QR",
        VerificationCode = "DCCC-E026-C28A-6D6D",
        Data = new InvoiceData
        {
            ReceiptType = receiptType,
            ReceiptGlobalNo = globalNo,
            ReceiptCounter = 12,
            ReceiptCurrency = "USD",
            InvoiceNo = $"{deviceId}-{globalNo}"
        }
    };

    /// <summary>The platform: holds what it is told to, and records what it is asked to file.</summary>
    private sealed class FakePlatform
    {
        public HashSet<string> Holds { get; } = [];

        public List<object> Filed { get; } = [];

        public IFiscalisationApiClient Client => StubProxy.For<IFiscalisationApiClient>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(IFiscalisationApiClient.CheckReceiptAsync):
                    var number = (string)args![1]!;
                    return Task.FromResult(new CheckFiscalisedReceiptApiResponse
                    {
                        IsFiscalised = Holds.Contains(number),
                        Matches = Holds.Contains(number)
                            ? [new FiscalisedReceiptRecordDto { InvoiceNo = number, ReceiptGlobalNo = 900, DeviceId = 46668 }]
                            : []
                    });
                case nameof(IFiscalisationApiClient.SubmitSapReceiptAsync):
                case nameof(IFiscalisationApiClient.SubmitReceiptAsync):
                    Filed.Add(args![0]!);
                    return Task.FromResult(new SubmitReceiptApiResponse());
                case nameof(IFiscalisationApiClient.PreflightReceiptAsync):
                    return Task.FromResult(new PreflightReceiptApiResponse { Valid = true });
                default:
                    throw new InvalidOperationException($"Platform {method.Name} was not expected.");
            }
        });
    }

    /// <summary>REVMax: answers GetInvoice from what it holds, and records any post.</summary>
    private sealed class FakeRevmax : Dictionary<string, InvoiceResponse>
    {
        public bool Unreachable { get; init; }

        /// <summary>The device's busy answer, in the same shape as "not found".</summary>
        public bool Busy { get; init; }

        public List<string> Asked { get; } = [];

        public List<string> Posted { get; } = [];

        public IRevmaxClient Client => StubProxy.For<IRevmaxClient>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(IRevmaxClient.GetInvoiceAsync):
                    var number = (string)args![0]!;
                    Asked.Add(number);
                    if (Unreachable)
                    {
                        return Task.FromException<InvoiceResponse?>(new HttpRequestException("unreachable"));
                    }

                    return Task.FromResult<InvoiceResponse?>(
                        TryGetValue(number, out var held) ? held
                        : new InvoiceResponse { Code = "0", Message = Busy ? "Init error -1" : "Invoice not Found" });
                case nameof(IRevmaxClient.TransactMAsync):
                    Posted.Add(method.Name);
                    return Task.FromResult<TransactMResponse?>(new TransactMResponse { Code = "1", Message = "Success" });
                case nameof(IRevmaxClient.TransactMExtAsync):
                    Posted.Add(method.Name);
                    return Task.FromResult<TransactMExtResponse?>(new TransactMExtResponse { Code = "1", Message = "Success" });
                default:
                    throw new InvalidOperationException($"REVMax {method.Name} was not expected.");
            }
        });
    }
}
