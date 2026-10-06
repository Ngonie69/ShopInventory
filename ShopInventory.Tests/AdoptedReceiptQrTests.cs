using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// A sale whose receipt was already filed is adopted rather than signed again. It used to be adopted with
/// its number alone, so the queue, the drawer and SAP's U_Fiscal_Url had no QR to show for it; the
/// platform's check carries the device signature the QR is composed from.
/// </summary>
public sealed class AdoptedReceiptQrTests
{
    private static readonly string Signature =
        Convert.ToBase64String(Enumerable.Range(1, 32).Select(b => (byte)b).ToArray());

    [Fact]
    public async Task An_adopted_receipt_carries_the_QR_and_code_a_fresh_one_would()
    {
        var found = await Service(new CheckFiscalisedReceiptApiResponse
        {
            IsFiscalised = true,
            Matches =
            [
                new FiscalisedReceiptRecordDto
                {
                    DeviceId = 46668,
                    FiscalDayNo = 41,
                    ReceiptGlobalNo = 1871,
                    InvoiceNo = "VAN005-INV-1",
                    ReceiptDate = new DateTime(2026, 10, 5, 12, 26, 0),
                    DeviceSignatureValue = Signature
                }
            ]
        }).FindPreSapReceiptAsync("VAN005-INV-1");

        var code = FiscalReceiptQrComposer.TryCreateVerificationCode(Signature);

        Assert.NotNull(found);
        Assert.True(found.Success);
        Assert.Equal("1871", found.ReceiptGlobalNo);
        Assert.Equal("41", found.FiscalDayNo);
        Assert.Equal("https://fdms.zimra.co.zw/0000046668051020260000001871" + code, found.QRCode);
        Assert.Equal(FiscalReceiptQrComposer.FormatVerificationCode(code!), found.VerificationCode);
        Assert.Equal("KEF-46668", found.DeviceSerial);
    }

    [Fact]
    public async Task A_receipt_the_platform_does_not_hold_is_not_adopted()
    {
        var found = await Service(new CheckFiscalisedReceiptApiResponse { IsFiscalised = false })
            .FindPreSapReceiptAsync("VAN005-INV-2");

        Assert.Null(found);
    }

    private static FiscalizationService Service(CheckFiscalisedReceiptApiResponse check) =>
        new(
            StubProxy.For<IFiscalisationApiClient>((method, _) =>
                method.Name == nameof(IFiscalisationApiClient.CheckReceiptAsync)
                    ? Task.FromResult(check)
                    : throw new InvalidOperationException($"IFiscalisationApiClient.{method.Name} was not expected.")),
            StubProxy.For<IFiscalDeviceConfigCache>((_, args) => Task.FromResult<FiscalConfigApiResponse?>(
                new FiscalConfigApiResponse { QrUrl = "https://fdms.zimra.co.zw/", DeviceSerialNo = $"KEF-{args![0]}" })),
            Options.Create(new FiscalisationSettings { Enabled = true }),
            Options.Create(new TaxSettings()),
            NullLogger<FiscalizationService>.Instance);
}
