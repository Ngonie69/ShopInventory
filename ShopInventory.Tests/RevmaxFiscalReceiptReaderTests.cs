using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The REVMax read-back must only ever claim a receipt our own device filed, of the kind asked
/// about. <c>GetInvoice</c> answers a number with whichever device's receipt carries it.
/// </summary>
public class RevmaxFiscalReceiptReaderTests
{
    private const int OurDeviceId = 22862;

    [Fact]
    public async Task Our_devices_receipt_is_read_as_fiscalised_with_its_qr_and_code()
    {
        var snapshot = await LookupAsync(Held("22862", "FiscalInvoice"), ReceiptType.FiscalInvoice);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsFiscalised);
        Assert.Equal(220062, snapshot.ReceiptGlobalNo);
        Assert.Equal("QR", snapshot.QrCode);
        Assert.Equal("DCCC-E026-C28A-6D6D", snapshot.VerificationCode);
        Assert.Equal("22862", snapshot.DeviceId);
    }

    [Theory]
    [InlineData("3180")]
    [InlineData("3044")]
    [InlineData(null)]
    [InlineData("")]
    public async Task Another_devices_receipt_is_read_as_not_fiscalised(string? deviceId)
    {
        var snapshot = await LookupAsync(Held(deviceId, "FiscalInvoice"), ReceiptType.FiscalInvoice);

        Assert.NotNull(snapshot);
        Assert.False(snapshot.IsFiscalised);
        Assert.Null(snapshot.ReceiptGlobalNo);
        Assert.Null(snapshot.QrCode);
        Assert.Null(snapshot.VerificationCode);
        Assert.Null(snapshot.DeviceId);
    }

    [Theory]
    [InlineData("CreditNote", ReceiptType.FiscalInvoice)]
    [InlineData("FiscalInvoice", ReceiptType.CreditNote)]
    [InlineData(null, ReceiptType.FiscalInvoice)]
    public async Task A_receipt_of_the_other_kind_is_read_as_not_fiscalised(string? heldType, ReceiptType askedType)
    {
        var snapshot = await LookupAsync(Held("22862", heldType), askedType);

        Assert.NotNull(snapshot);
        Assert.False(snapshot.IsFiscalised);
        Assert.Null(snapshot.ReceiptGlobalNo);
    }

    [Fact]
    public async Task A_credit_note_on_our_device_is_read_as_fiscalised_when_one_was_asked_about()
    {
        var snapshot = await LookupAsync(Held("22862", "CreditNote"), ReceiptType.CreditNote);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsFiscalised);
    }

    [Fact]
    public async Task Invoice_not_found_is_still_read_as_not_fiscalised()
    {
        var snapshot = await LookupAsync(
            new InvoiceResponse { Code = "0", Message = "Invoice not Found" }, ReceiptType.FiscalInvoice);

        Assert.NotNull(snapshot);
        Assert.False(snapshot.IsFiscalised);
    }

    [Fact]
    public async Task No_answer_is_still_a_failed_lookup()
    {
        Assert.Null(await LookupAsync(null, ReceiptType.FiscalInvoice));
    }

    private static InvoiceResponse Held(string? deviceId, string? receiptType) => new()
    {
        Code = "1",
        DeviceID = deviceId,
        DeviceSerialNumber = "8DE6996C0188",
        QRcode = "QR",
        VerificationCode = "DCCC-E026-C28A-6D6D",
        FiscalDay = "535",
        Data = new InvoiceData { ReceiptType = receiptType, ReceiptGlobalNo = 220062 }
    };

    private static Task<FiscalReceiptSnapshot?> LookupAsync(InvoiceResponse? response, ReceiptType receiptType)
    {
        var client = StubProxy.For<IRevmaxClient>((method, _) => method.Name == "GetInvoiceAsync"
            ? Task.FromResult(response)
            : throw new InvalidOperationException("Unexpected device call"));
        var reader = new RevmaxFiscalReceiptReader(
            client, new RevmaxSettings { Enabled = true, DefaultRefDeviceId = OurDeviceId });

        return reader.TryLookupAsync(776179, receiptType, NullLogger.Instance, default);
    }
}
