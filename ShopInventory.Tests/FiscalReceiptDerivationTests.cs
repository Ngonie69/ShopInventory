using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Golden vectors for the canonical payload a fiscal receipt's device signature is taken over.
///
/// Copied verbatim from the fiscalisation platform's own <c>ReceiptCanonicalPayloadTests</c>, which is
/// the source of truth. Three implementations have to agree on this string to the character — the
/// platform's signer, its pre-signed ingest verifier, and this copy — and a disagreement is invisible
/// everywhere it could be caught cheaply. A receipt signed over a subtly different payload prints
/// correctly, produces a QR that scans, and is refused by ZIMRA only when the fiscal day's offline file
/// is uploaded, by which time a day of customers have gone home holding invalid receipts.
///
/// <b>If one of these fails, do not edit the expected literal to match the code.</b> Work out which side
/// moved. If the platform's vectors changed, re-copy them here and into the handset's test project.
/// </summary>
public sealed class FiscalReceiptDerivationTests
{
    private const int DeviceId = 12345;

    /// <summary>Base64 of "previous-hash" — the shape of a real chained hash, with padding.</summary>
    private const string PreviousReceiptHash = "cHJldmlvdXMtaGFzaA==";

    private const string ExpectedPayload =
        "12345FISCALINVOICEUSD43212026-08-10T14:30:0512345A01000C15.00161112345cHJldmlvdXMtaGFzaA==";

    private const string ExpectedHashBase64 = "K9HAC/UQ/781L6VzhHVEb4llGrgSzm04UycPD09TNVk=";

    private static readonly DateTime GoldenReceiptDate =
        new(2026, 8, 10, 14, 30, 5, DateTimeKind.Unspecified);

    /// <summary>
    /// Taxes chosen to exercise every rule with a plausible wrong answer: supplied out of id order, a
    /// code needing trim and upper-casing, and one tax with no percentage at all rather than a zero one.
    /// </summary>
    private static DerivedTax[] GoldenTaxes() =>
    [
        new(TaxId: 3, TaxPercent: 15.00m, TaxCode: " c ", TaxAmount: 16.11m, SalesAmountWithTax: 123.445m),
        new(TaxId: 1, TaxPercent: null, TaxCode: "a", TaxAmount: 0m, SalesAmountWithTax: 10.00m)
    ];

    private static string BuildGoldenPayload(string? previousHash) =>
        FiscalReceiptDerivation.BuildCanonicalPayload(
            DeviceId,
            ReceiptType.FiscalInvoice,
            " usd ",
            receiptGlobalNo: 4321,
            GoldenReceiptDate,
            receiptTotal: 123.445m,
            GoldenTaxes(),
            previousHash);

    [Fact]
    public void CanonicalPayload_MatchesTheGoldenVector()
    {
        Assert.Equal(ExpectedPayload, BuildGoldenPayload(PreviousReceiptHash));
    }

    [Fact]
    public void CanonicalPayload_HashesToTheGoldenValue()
    {
        Assert.Equal(
            ExpectedHashBase64,
            FiscalReceiptDerivation.ComputeSignatureHash(BuildGoldenPayload(PreviousReceiptHash)));
    }

    /// <summary>
    /// The first receipt of a fiscal day starts a fresh chain. A blank hash must behave identically to a
    /// null one, or a client that sends "" rather than omitting the field signs a payload the server
    /// does not verify.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanonicalPayload_AppendsNothingWhenThereIsNoPreviousHash(string? previousHash)
    {
        Assert.Equal(
            "12345FISCALINVOICEUSD43212026-08-10T14:30:0512345A01000C15.00161112345",
            BuildGoldenPayload(previousHash));
    }

    /// <summary>
    /// The order taxes arrive in is the caller's business; the order they are signed in is not. Nothing
    /// makes the independent groupings in this solution, the platform and the handset emit one order.
    /// </summary>
    [Fact]
    public void CanonicalPayload_IsIndependentOfTheOrderTaxesArriveIn()
    {
        var forwards = FiscalReceiptDerivation.ConcatenateTaxes(GoldenTaxes());
        var backwards = FiscalReceiptDerivation.ConcatenateTaxes(GoldenTaxes().Reverse());

        Assert.Equal(forwards, backwards);
    }

    /// <summary>
    /// 123.445 is 12345 cents, not 12344. .NET rounds half to even by default, which disagrees on
    /// exactly the half-cent cases — often enough to break a trading day, rarely enough to survive a
    /// test suite that does not look for it.
    /// </summary>
    [Theory]
    [InlineData(123.445, "12345")]
    [InlineData(0.005, "1")]
    [InlineData(0.015, "2")]
    [InlineData(-1.005, "-101")]
    [InlineData(0, "0")]
    public void AmountsAreWholeCentsRoundedHalfAwayFromZero(decimal amount, string expected)
    {
        Assert.Equal(expected, FiscalReceiptDerivation.FormatAmountInCents(amount));
    }

    /// <summary>
    /// Null is untaxed and zero is a zero rate. They are signed differently and must stay distinct: a
    /// client that normalises one to the other produces a signature the platform will not verify.
    /// </summary>
    [Fact]
    public void ANullTaxPercentIsNotAZeroOne()
    {
        Assert.Equal(string.Empty, FiscalReceiptDerivation.FormatTaxPercent(null));
        Assert.Equal("0.00", FiscalReceiptDerivation.FormatTaxPercent(0m));
    }

    // --- The derivation the platform performs on the lines it is sent ---

    private static SubmitReceiptApiRequest RequestWith(bool taxInclusive, params LineApiRequest[] lines) =>
        new() { DeviceId = DeviceId, TaxInclusive = taxInclusive, Lines = [.. lines] };

    private static LineApiRequest Line(decimal price, decimal quantity, int taxId, decimal? percent, string? code = "O01") =>
        new() { Name = "Item", Price = price, Quantity = quantity, TaxId = taxId, TaxPercent = percent, TaxCode = code };

    /// <summary>
    /// Tax is extracted from the line total, not added to it, and the tax block is what gets signed.
    /// 15.5% inclusive on 115.50 is 15.50, and the sales amount stays the gross figure.
    /// </summary>
    [Fact]
    public void TaxInclusiveLinesExtractTaxRatherThanAddIt()
    {
        var derived = FiscalReceiptDerivation.Derive(
            RequestWith(taxInclusive: true, Line(115.50m, 1m, 517, 15.5m)));

        var tax = Assert.Single(derived.Taxes);
        Assert.Equal(15.50m, tax.TaxAmount);
        Assert.Equal(115.50m, tax.SalesAmountWithTax);
        Assert.Equal(115.50m, derived.ReceiptTotal);
    }

    [Fact]
    public void TaxExclusiveLinesAddTaxOnTop()
    {
        var derived = FiscalReceiptDerivation.Derive(
            RequestWith(taxInclusive: false, Line(100m, 1m, 517, 15.5m)));

        var tax = Assert.Single(derived.Taxes);
        Assert.Equal(15.50m, tax.TaxAmount);
        Assert.Equal(115.50m, tax.SalesAmountWithTax);
        Assert.Equal(115.50m, derived.ReceiptTotal);
    }

    /// <summary>
    /// Each line is rounded before the group is summed, not after. Summing first and rounding once
    /// gives a different tax base, and the difference is what FDMS sees as a payment that does not
    /// match the receipt.
    /// </summary>
    [Fact]
    public void LineTotalsAreRoundedBeforeTheyAreSummed()
    {
        var derived = FiscalReceiptDerivation.Derive(
            RequestWith(taxInclusive: true, Line(1.005m, 1m, 517, 15.5m), Line(1.005m, 1m, 517, 15.5m)));

        // 1.005 rounds away from zero to 1.01 twice, so the base is 2.02 — not 2.01, which is what
        // rounding the 2.010 sum once would give.
        Assert.Equal(2.02m, derived.ReceiptTotal);
    }

    /// <summary>
    /// Lines sharing a tax id but differing in rate are separate groups, because a rate change mid-day
    /// gives one id two percentages and they are declared separately.
    /// </summary>
    [Fact]
    public void LinesGroupOnTheWholeTaxTripleNotJustTheId()
    {
        var derived = FiscalReceiptDerivation.Derive(RequestWith(
            taxInclusive: true,
            Line(115.50m, 1m, 517, 15.5m),
            Line(115.00m, 1m, 517, 15.0m)));

        Assert.Equal(2, derived.Taxes.Count);
    }

    /// <summary>
    /// The payment a fiscal invoice declares is the total derived from its lines, never an upstream
    /// figure. A total carried over from a document that rounds differently arrives at FDMS as a
    /// few-cent discrepancy between the receipt and its payment.
    /// </summary>
    [Fact]
    public void ThePaymentIsAnchoredToTheDerivedTotal()
    {
        var derived = FiscalReceiptDerivation.Derive(
            RequestWith(taxInclusive: true, Line(19.99m, 3m, 517, 15.5m)));

        Assert.Equal(59.97m, derived.ReceiptTotal);
        Assert.Equal(derived.ReceiptTotal, derived.PaymentAmount);
    }
}
