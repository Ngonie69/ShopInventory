using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// The rules that decide whether a receipt is worth sending, checked before it is.
///
/// Two different stakes are being protected here and they pull in opposite directions, which is why the
/// severity of each rule is asserted and not just its presence.
///
/// A rule that blocks when it should not is the more expensive mistake. A blocked signed receipt is
/// marked unsubmittable, and because a device's receipts are hash-chained, everything that handset signed
/// afterwards is stuck behind it — so a single over-eager rule stops a van's whole day, and a rule that is
/// wrong for every receipt stops the fleet. A rule that fails to block merely costs one round trip to be
/// told the same thing by the platform.
/// </summary>
public sealed class ReceiptPreflightTests
{
    private const int DeviceId = 35410;

    private static readonly DateTime Now = new(2026, 8, 10, 14, 30, 0, DateTimeKind.Unspecified);

    /// <summary>A VAT-registered taxpayer with the standard rate and a zero rate, as this company is.</summary>
    private static FiscalConfigApiResponse VatPayerConfig(string mode = "Offline") => new()
    {
        DeviceSerialNo = "0000035410",
        DeviceOperatingMode = mode,
        VatNumber = "220123456",
        TaxPayerTIN = "2001234567",
        TaxPayerDayMaxHrs = 24,
        CertificateValidTill = Now.AddYears(1),
        ApplicableTaxes =
        [
            new FiscalTaxDto { TaxID = 517, TaxPercent = 15.5m, TaxName = "15.5% Output VAT USD" },
            new FiscalTaxDto { TaxID = 2, TaxPercent = 0m, TaxName = "Zero rated" }
        ]
    };

    private static PreflightContext Context(FiscalConfigApiResponse? config, DateTime? dayOpened = null) =>
        new(config, Now, FiscalDayOpen: true, FiscalDayOpenedAt: dayOpened);

    private static LineApiRequest Line(
        decimal price = 115.50m,
        int taxId = 517,
        decimal? percent = 15.5m,
        string? hsCode = "04031000",
        string? taxCode = "O01") => new()
        {
            Name = "Cheese 1kg",
            Price = price,
            Quantity = 1m,
            TaxId = taxId,
            TaxPercent = percent,
            HsCode = hsCode,
            TaxCode = taxCode
        };

    private static IngestSignedReceiptApiRequest Signed(params LineApiRequest[] lines) => new()
    {
        DeviceId = DeviceId,
        InvoiceNo = "VAN006-INV-20260810-D261C8",
        Currency = "USD",
        ReceiptDate = Now,
        TaxInclusive = true,
        ReceiptType = ReceiptType.FiscalInvoice,
        FiscalDayNo = 19,
        FiscalDayOpenedAt = Now.AddHours(-6),
        ReceiptCounter = 4,
        ReceiptGlobalNo = 501,
        DeviceSignatureHash = "hash",
        DeviceSignatureValue = "signature",
        Lines = lines.Length == 0 ? [Line()] : [.. lines]
    };

    private static void AssertBlocks(PreflightReport report, string code)
    {
        Assert.True(
            report.Blocks.Any(finding => finding.Code == code),
            $"Expected a {code} block. Findings: {(report.Findings.Count == 0 ? "(none)" : report.Summary)}");
    }

    private static void AssertDoesNotBlock(PreflightReport report, string code)
    {
        Assert.False(
            report.Blocks.Any(finding => finding.Code == code),
            $"Did not expect a {code} block. Findings: {report.Summary}");
    }

    // --- Tax ---

    /// <summary>
    /// A tax id the device does not have is refused outright by FDMS. Worth catching first because the
    /// lease is what handed the handset its tax table, so this fires when the two have drifted.
    /// </summary>
    [Fact]
    public void ATaxIdTheDeviceDoesNotHaveIsBlocked()
    {
        var report = ReceiptPreflight.InspectSigned(Signed(Line(taxId: 3)), Context(VatPayerConfig()));

        AssertBlocks(report, "RCPT025");
    }

    /// <summary>
    /// The worst failure in the set, because FDMS accepts it. A tax id paired with the wrong percentage
    /// is filed under the wrong rate and under-declares VAT, and nothing ever reports an error.
    /// </summary>
    [Fact]
    public void ATaxPercentThatDoesNotMatchItsIdIsBlocked()
    {
        var report = ReceiptPreflight.InspectSigned(
            Signed(Line(taxId: 517, percent: 14.5m)),
            Context(VatPayerConfig()));

        AssertBlocks(report, "RCPT025");
    }

    [Fact]
    public void AMatchingTaxIdAndPercentPass()
    {
        var report = ReceiptPreflight.InspectSigned(Signed(), Context(VatPayerConfig()));

        Assert.False(report.IsBlocked, report.Summary);
    }

    // --- HS codes ---

    [Fact]
    public void AVatPayerMustGiveAnHsCodeOnEveryInvoiceLine()
    {
        var report = ReceiptPreflight.InspectSigned(Signed(Line(hsCode: null)), Context(VatPayerConfig()));

        AssertBlocks(report, "RCPT047");
    }

    /// <summary>A zero-rated line from a VAT payer needs the full eight digits — four is not enough.</summary>
    [Fact]
    public void AZeroRatedLineNeedsAnEightDigitHsCode()
    {
        var report = ReceiptPreflight.InspectSigned(
            Signed(Line(taxId: 2, percent: 0m, hsCode: "0403")),
            Context(VatPayerConfig()));

        AssertBlocks(report, "RCPT048");
    }

    [Fact]
    public void AStandardRatedLineAcceptsFourDigits()
    {
        var report = ReceiptPreflight.InspectSigned(Signed(Line(hsCode: "0403")), Context(VatPayerConfig()));

        AssertDoesNotBlock(report, "RCPT048");
    }

    [Fact]
    public void AnHsCodeWithNonDigitsIsBlocked()
    {
        var report = ReceiptPreflight.InspectSigned(Signed(Line(hsCode: "0403-10")), Context(VatPayerConfig()));

        AssertBlocks(report, "RCPT048");
    }

    // --- Currency, and the asymmetry between the two submission paths ---

    /// <summary>
    /// The platform normalises a currency it is going to sign itself, but cannot normalise one already
    /// inside a signature. So a lower-case code that the submit path would silently fix is a refusal on
    /// the pre-signed path.
    /// </summary>
    [Theory]
    [InlineData("usd")]
    [InlineData(" USD ")]
    public void APreSignedReceiptMustCarryAnAlreadyNormalisedCurrency(string currency)
    {
        var request = Signed();
        request.Currency = currency;

        AssertBlocks(ReceiptPreflight.InspectSigned(request, Context(VatPayerConfig())), "RCPT010");
    }

    /// <summary>SAP calls it ZiG, FDMS only knows ZWG, and the handset has to convert before signing.</summary>
    [Fact]
    public void ZigIsBlockedBecauseFdmsOnlyKnowsZwg()
    {
        var request = Signed();
        request.Currency = "ZIG";

        AssertBlocks(ReceiptPreflight.InspectSigned(request, Context(VatPayerConfig())), "RCPT010");
    }

    [Fact]
    public void ZwgIsAccepted()
    {
        var request = Signed();
        request.Currency = "ZWG";

        AssertDoesNotBlock(ReceiptPreflight.InspectSigned(request, Context(VatPayerConfig())), "RCPT010");
    }

    // --- Line shape ---

    [Fact]
    public void AnInvoiceLinePricedAtOrBelowZeroIsBlocked()
    {
        var report = ReceiptPreflight.InspectSigned(Signed(Line(price: 0m)), Context(VatPayerConfig()));

        Assert.True(report.IsBlocked);
    }

    [Fact]
    public void AReceiptWithNoInvoiceNumberIsBlockedBecauseItIsTheIdempotencyKey()
    {
        var request = Signed();
        request.InvoiceNo = "  ";

        AssertBlocks(ReceiptPreflight.InspectSigned(request, Context(VatPayerConfig())), "IDEMPOTENCY_KEY_REQUIRED");
    }

    /// <summary>
    /// The van lease hands the handset the FDMS tax NAME as its tax code ("15.5% Output VAT USD"), which
    /// is far longer than the platform's 3-character ceiling. That ceiling is enforced only on the submit
    /// path, and could not apply here anyway because the code is inside the signature — so applying it to
    /// signed receipts would refuse the entire fleet's takings.
    /// </summary>
    [Fact]
    public void ALongTaxCodeDoesNotBlockAPreSignedReceipt()
    {
        var report = ReceiptPreflight.InspectSigned(
            Signed(Line(taxCode: "15.5% Output VAT USD")),
            Context(VatPayerConfig()));

        Assert.False(report.IsBlocked, report.Summary);
    }

    [Fact]
    public void ALongTaxCodeDoesBlockOnThePathThatEnforcesIt()
    {
        var request = new SubmitReceiptApiRequest
        {
            DeviceId = DeviceId,
            InvoiceNo = "SI-1001",
            Currency = "USD",
            ReceiptDate = Now,
            TaxInclusive = true,
            Lines = [Line(taxCode: "15.5% Output VAT USD")]
        };

        Assert.True(ReceiptPreflight.Inspect(request, Context(VatPayerConfig())).IsBlocked);
    }

    // --- Device and fiscal day ---

    /// <summary>
    /// Only a device ZIMRA registered in Offline mode may hand over receipts it signed itself. In Online
    /// mode FDMS owns the sequence, so the handset's numbering is not the device's.
    /// </summary>
    [Fact]
    public void AnOnlineModeDeviceCannotHandOverItsOwnSignedReceipts()
    {
        var report = ReceiptPreflight.InspectSigned(Signed(), Context(VatPayerConfig(mode: "Online")));

        AssertBlocks(report, "DEV01");
    }

    [Fact]
    public void AFiscalDayPastTheTaxpayersLimitIsBlocked()
    {
        var report = ReceiptPreflight.InspectSigned(
            Signed(),
            Context(VatPayerConfig(), dayOpened: Now.AddHours(-25)));

        AssertBlocks(report, "RCPT041");
    }

    [Fact]
    public void AFiscalDayApproachingTheLimitWarnsWithoutBlocking()
    {
        var report = ReceiptPreflight.InspectSigned(
            Signed(),
            Context(VatPayerConfig(), dayOpened: Now.AddHours(-20)));

        Assert.False(report.IsBlocked, report.Summary);
        Assert.Contains(report.Warnings, finding => finding.Code == "FiscalDayNearingLimit");
    }

    [Fact]
    public void AnExpiredCertificateIsBlocked()
    {
        var config = VatPayerConfig();
        config.CertificateValidTill = Now.AddDays(-1);

        AssertBlocks(ReceiptPreflight.InspectSigned(Signed(), Context(config)), "DeviceCertificateExpired");
    }

    // --- Signature ---

    [Fact]
    public void AReceiptWithNoSignatureIsBlocked()
    {
        var request = Signed();
        request.DeviceSignatureHash = null;
        request.DeviceSignatureValue = null;

        AssertBlocks(ReceiptPreflight.InspectSigned(request, Context(VatPayerConfig())), "SignatureMissing");
    }

    /// <summary>
    /// A signature that does not cover the receipt as stored is reported and still submitted — the single
    /// most important severity decision in this file.
    ///
    /// What is compared is a reconstruction from database columns, not the bytes the handset held: a price
    /// rounded on storage, a tax percent stored as null where the handset had zero, or lines read back in
    /// another order would all diverge without anything being wrong with the receipt. Blocking would mark
    /// it permanently unsubmittable and, because receipts are chained, stop every later receipt from that
    /// handset — so one storage-fidelity bug would halt the fleet's fiscalisation on a verdict this
    /// application is not the authority for. The platform holds the certificate; it decides.
    /// </summary>
    [Fact]
    public void ASignatureThatDoesNotMatchWarnsButNeverBlocks()
    {
        var report = ReceiptPreflight.InspectSigned(Signed(), Context(VatPayerConfig()));

        Assert.Contains(report.Warnings, finding => finding.Code == "SignaturePayloadMismatch");
        Assert.False(report.IsBlocked, report.Summary);
    }

    // --- Degrading rather than guessing ---

    /// <summary>
    /// With no device configuration nothing that depends on it can be judged, and inventing a verdict
    /// either way would be worse than saying so. The platform validates the receipt again on arrival.
    /// </summary>
    [Fact]
    public void AnUnreachableDeviceConfigurationWarnsAndChecksWhatItStillCan()
    {
        var report = ReceiptPreflight.InspectSigned(Signed(Line(taxId: 9999)), Context(config: null));

        Assert.Contains(report.Warnings, finding => finding.Code == "ConfigUnavailable");
        AssertDoesNotBlock(report, "RCPT025");
    }

    /// <summary>
    /// A non-VAT taxpayer may not charge a VAT rate. Checked because the tax table is per taxpayer and a
    /// device moved between environments keeps its old mappings.
    /// </summary>
    [Fact]
    public void ANonVatTaxpayerCannotChargeVat()
    {
        var config = VatPayerConfig();
        config.VatNumber = null;

        AssertBlocks(ReceiptPreflight.InspectSigned(Signed(), Context(config)), "RCPT021");
    }
}
