using System.Text.Json;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the JSON this service sends for a device-signed fiscal day close.
///
/// The platform binds these property names onto its own request model, and nothing on either side fails
/// loudly when they disagree: a member that binds to nothing leaves the platform signing the close
/// itself, which for a van handset it cannot do. The day then never closes, and the only symptom is a
/// fiscal day that stays open forever.
///
/// The expected names here are copied from the platform's <c>DeclaredFiscalDayCloseApiRequest</c>. If it
/// renames one, this test is what says so.
/// </summary>
public sealed class DeclaredFiscalDayCloseWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Request_SendsTheDeclaredCloseUnderTheNamesThePlatformBinds()
    {
        var request = new GenerateOfflineFileApiRequest
        {
            DeviceId = 36189,
            FiscalDayNo = 12,
            CloseFiscalDay = true,
            DeclaredClose = new DeclaredFiscalDayCloseApiRequest
            {
                SignatureHash = "aGFzaA==",
                SignatureValue = "c2lnbmF0dXJl",
                Counters =
                [
                    new DeclaredFiscalDayCounterApiRequest
                    {
                        FiscalCounterType = "SaleByTax",
                        FiscalCounterCurrency = "USD",
                        FiscalCounterTaxID = 1,
                        FiscalCounterTaxPercent = 15.00m,
                        FiscalCounterValue = 200.00m
                    }
                ]
            }
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, Web));
        var declared = document.RootElement.GetProperty("declaredClose");

        Assert.Equal("aGFzaA==", declared.GetProperty("signatureHash").GetString());
        Assert.Equal("c2lnbmF0dXJl", declared.GetProperty("signatureValue").GetString());

        var counter = Assert.Single(declared.GetProperty("counters").EnumerateArray().ToList());

        // Named, not numeric: the platform accepts both, and a name survives an enum being reordered.
        Assert.Equal("SaleByTax", counter.GetProperty("fiscalCounterType").GetString());
        Assert.Equal("USD", counter.GetProperty("fiscalCounterCurrency").GetString());
        Assert.Equal(1, counter.GetProperty("fiscalCounterTaxID").GetInt32());
        Assert.Equal(15.00m, counter.GetProperty("fiscalCounterTaxPercent").GetDecimal());
        Assert.Equal(200.00m, counter.GetProperty("fiscalCounterValue").GetDecimal());
    }

    /// <summary>
    /// A device the platform signs for must not send an empty declared close. Null means "you sign it";
    /// an empty object means "I declare the day sold nothing", and the platform would refuse the day.
    /// </summary>
    [Fact]
    public void Request_OmitsTheDeclaredCloseForAPlatformSignedDevice()
    {
        var request = new GenerateOfflineFileApiRequest
        {
            DeviceId = 35410,
            FiscalDayNo = 19,
            CloseFiscalDay = true
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, Web));

        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("declaredClose").ValueKind);
    }

    /// <summary>
    /// An absent tax percentage stays absent. Null and zero are different counters to FDMS — null is
    /// untaxed, zero is zero-rated — and the device signed whichever it sent.
    /// </summary>
    [Fact]
    public void Request_KeepsAnAbsentTaxPercentageAbsent()
    {
        var request = new GenerateOfflineFileApiRequest
        {
            DeviceId = 36189,
            FiscalDayNo = 12,
            CloseFiscalDay = true,
            DeclaredClose = new DeclaredFiscalDayCloseApiRequest
            {
                Counters =
                [
                    new DeclaredFiscalDayCounterApiRequest
                    {
                        FiscalCounterType = "BalanceByMoneyType",
                        FiscalCounterCurrency = "USD",
                        FiscalCounterMoneyType = "Cash",
                        FiscalCounterValue = 200.00m
                    }
                ]
            }
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request, Web));
        var counter = document.RootElement
            .GetProperty("declaredClose")
            .GetProperty("counters")[0];

        Assert.Equal(JsonValueKind.Null, counter.GetProperty("fiscalCounterTaxPercent").ValueKind);
        Assert.Equal("Cash", counter.GetProperty("fiscalCounterMoneyType").GetString());
    }
}
