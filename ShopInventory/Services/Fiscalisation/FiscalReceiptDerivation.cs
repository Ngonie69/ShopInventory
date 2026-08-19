using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// Works out what a receipt's line totals, tax block and total will be once the platform has recomputed
/// them, and rebuilds the exact string a device signature is taken over.
///
/// A copy, field for field, of the platform's <c>FiscalIntegrationEndpoints.ReceiptBuilder</c> and
/// <c>Fiscalisation.Domain.Signatures.ReceiptCanonicalPayload</c>. There is no shared contracts package,
/// the same way <see cref="SubmitReceiptApiRequest"/> and its neighbours are copies of the wire DTOs —
/// but the stakes here are higher, because a divergence in these rules is not a compile error or a
/// rejected request. It produces
/// a receipt that prints, scans and is refused by ZIMRA a day later when the fiscal day's offline file is
/// uploaded, by which time every customer has gone home with a printed copy.
///
/// So three implementations must agree to the character: this one, the platform's, and the handset's port.
/// <c>FiscalReceiptDerivationTests</c> pins this one against the platform's published golden vectors; if
/// the platform's change, those vectors are the thing to re-copy first.
/// </summary>
/// <remarks>
/// Two traps are worth naming, because both survive casual testing and both are wrong every time money
/// lands on a half cent:
///
/// <list type="bullet">
/// <item>
/// Rounding is <see cref="MidpointRounding.AwayFromZero"/> everywhere. .NET's default is banker's
/// rounding, which agrees with this on all but the exact-half cases — frequent enough in a real trading
/// day to break one, rare enough to pass a test suite that does not look for it.
/// </item>
/// <item>
/// A null tax percent and a zero tax percent are different values, not two spellings of nothing. Null is
/// untaxed and contributes an empty string; zero is a real zero rate and contributes <c>"0.00"</c>.
/// </item>
/// </list>
/// </remarks>
public static class FiscalReceiptDerivation
{
    /// <summary>
    /// Second precision, local wall clock. FDMS compares a receipt's date against the fiscal day it
    /// belongs to, both in the taxpayer's own time; a UTC value moves receipts across the day boundary.
    /// </summary>
    public const string ReceiptDateFormat = "yyyy-MM-ddTHH:mm:ss";

    /// <summary>
    /// Recomputes the receipt the way the platform will, from the lines as sent.
    /// </summary>
    /// <remarks>
    /// The platform ignores whatever totals a caller computed and derives its own from the lines, so
    /// these are the figures that end up signed. Anything that needs to predict a signature, a total or a
    /// tax split has to ask here rather than trusting the numbers it was handed.
    /// </remarks>
    public static DerivedReceipt Derive(SubmitReceiptApiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lines = request.Lines
            .Select((line, index) => new DerivedLine(
                LineNo: index + 1,
                Name: line.Name?.Trim() ?? string.Empty,
                HsCode: string.IsNullOrWhiteSpace(line.HsCode) ? null : line.HsCode.Trim(),
                Quantity: line.Quantity,
                Price: line.Price,
                LineTotal: Round(line.Price * line.Quantity),
                TaxId: line.TaxId,
                TaxPercent: line.TaxPercent,
                TaxCode: string.IsNullOrWhiteSpace(line.TaxCode) ? null : line.TaxCode.Trim()))
            .ToList();

        // Grouped on the trimmed tax code without folding case, matching the platform's anonymous-type
        // key. The canonical payload upper-cases the code afterwards, so two lines differing only in the
        // case of their tax code are two groups here that render identically there — which is a real
        // difference in the signed string, and the reason this comparison is ordinal rather than tidy.
        var taxGroups = lines
            .GroupBy(line => (line.TaxId, line.TaxPercent, line.TaxCode))
            .Select(group =>
            {
                var salesTotal = group.Sum(line => line.LineTotal);
                var percent = group.Key.TaxPercent;

                var taxAmount = percent is > 0m
                    ? request.TaxInclusive
                        ? Round(salesTotal * percent.Value / (100m + percent.Value))
                        : Round(salesTotal * percent.Value / 100m)
                    : 0m;

                return new DerivedTax(
                    TaxId: group.Key.TaxId,
                    TaxPercent: percent,
                    TaxCode: group.Key.TaxCode,
                    TaxAmount: taxAmount,
                    SalesAmountWithTax: request.TaxInclusive ? salesTotal : salesTotal + taxAmount);
            })
            .ToList();

        var receiptTotal = request.TaxInclusive
            ? lines.Sum(line => line.LineTotal)
            : taxGroups.Sum(tax => tax.SalesAmountWithTax);

        return new DerivedReceipt(lines, taxGroups, receiptTotal);
    }

    /// <summary>
    /// The exact string a device signature is taken over: no separators, in this order, ending with the
    /// hash of the receipt before it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>previousReceiptHash</c> is the preceding receipt's hash for this device and fiscal day, as the
    /// stored base64 <em>text</em> — it is appended as-is, never decoded. Null, empty and whitespace all
    /// append nothing, which is what the first receipt of a day does.
    /// </para>
    /// <para>
    /// <c>receiptCounter</c> is deliberately absent. Only the global number is signed; the counter is
    /// validated separately, and adding it here would break every signature.
    /// </para>
    /// </remarks>
    public static string BuildCanonicalPayload(
        int deviceId,
        ReceiptType receiptType,
        string? currency,
        int receiptGlobalNo,
        DateTime receiptDate,
        decimal receiptTotal,
        IEnumerable<DerivedTax> taxes,
        string? previousReceiptHash)
    {
        ArgumentNullException.ThrowIfNull(taxes);

        var builder = new StringBuilder();
        builder.Append(deviceId.ToString(CultureInfo.InvariantCulture));
        builder.Append(receiptType.ToString().ToUpperInvariant());
        builder.Append((currency ?? string.Empty).Trim().ToUpperInvariant());
        builder.Append(receiptGlobalNo.ToString(CultureInfo.InvariantCulture));
        builder.Append(receiptDate.ToString(ReceiptDateFormat, CultureInfo.InvariantCulture));
        builder.Append(FormatAmountInCents(receiptTotal));
        builder.Append(ConcatenateTaxes(taxes));

        if (!string.IsNullOrWhiteSpace(previousReceiptHash))
        {
            builder.Append(previousReceiptHash.Trim());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Taxes ordered by id then by normalised code, each contributing code, percent, tax and sales amount.
    /// </summary>
    /// <remarks>
    /// The secondary ordinal sort is load-bearing: one tax id can carry two percentages when a rate
    /// changes mid-day, and a culture-sensitive comparison would order them differently depending on
    /// where the process happens to run.
    /// </remarks>
    public static string ConcatenateTaxes(IEnumerable<DerivedTax> taxes)
    {
        ArgumentNullException.ThrowIfNull(taxes);

        var builder = new StringBuilder();

        foreach (var tax in taxes
                     .OrderBy(t => t.TaxId)
                     .ThenBy(t => NormalizeTaxCode(t.TaxCode), StringComparer.Ordinal))
        {
            builder.Append(NormalizeTaxCode(tax.TaxCode));
            builder.Append(FormatTaxPercent(tax.TaxPercent));
            builder.Append(FormatAmountInCents(tax.TaxAmount));
            builder.Append(FormatAmountInCents(tax.SalesAmountWithTax));
        }

        return builder.ToString();
    }

    /// <summary>Base64 SHA-256 of the canonical payload, UTF-8 with no byte order mark.</summary>
    public static string ComputeSignatureHash(string canonicalPayload)
    {
        ArgumentNullException.ThrowIfNull(canonicalPayload);

        return Convert.ToBase64String(SHA256.HashData(new UTF8Encoding(false).GetBytes(canonicalPayload)));
    }

    public static string NormalizeTaxCode(string? taxCode) =>
        string.IsNullOrWhiteSpace(taxCode) ? string.Empty : taxCode.Trim().ToUpperInvariant();

    /// <summary>An absent percentage contributes nothing at all — not <c>"0.00"</c>, which is a real rate.</summary>
    public static string FormatTaxPercent(decimal? taxPercent) =>
        taxPercent.HasValue
            ? taxPercent.Value.ToString("0.00", CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>Whole cents, rounded half away from zero.</summary>
    public static string FormatAmountInCents(decimal amount) =>
        decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>The receipt as the platform will recompute it.</summary>
public sealed record DerivedReceipt(
    IReadOnlyList<DerivedLine> Lines,
    IReadOnlyList<DerivedTax> Taxes,
    decimal ReceiptTotal)
{
    /// <summary>
    /// What the payment must be. A fiscal invoice is paid in full, and the platform anchors the payment
    /// to the total it computed rather than to any figure supplied with the request — a total derived
    /// from an upstream document that rounds differently is what reaches FDMS as a few-cent gap.
    /// </summary>
    public decimal PaymentAmount => ReceiptTotal;

    public decimal TotalTax => Taxes.Sum(tax => tax.TaxAmount);
}

public sealed record DerivedLine(
    int LineNo,
    string Name,
    string? HsCode,
    decimal Quantity,
    decimal Price,
    decimal LineTotal,
    int TaxId,
    decimal? TaxPercent,
    string? TaxCode);

public sealed record DerivedTax(
    int TaxId,
    decimal? TaxPercent,
    string? TaxCode,
    decimal TaxAmount,
    decimal SalesAmountWithTax);
