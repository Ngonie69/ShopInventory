namespace ShopInventory.Configuration;

/// <summary>
/// Tax rates used for order and invoice calculations.
/// </summary>
/// <remarks>
/// This used to live on the REVMax settings, which made it look like a fiscal-device setting. It is
/// not — ordinary order and invoice tax maths reads it, independently of whether anything is being
/// fiscalised.
/// </remarks>
public class TaxSettings
{
    public const string SectionName = "Tax";

    /// <summary>
    /// VAT rate as a decimal (15.5% = 0.155). Effective 1 January 2026.
    /// </summary>
    /// <remarks>
    /// The standard rate, and the rate for a line whose tax code is unknown or absent. That fallback
    /// matches the fiscalisation settings' own <c>DefaultTaxId</c>, which is the standard-rated id.
    /// </remarks>
    public decimal VatRate { get; set; } = 0.155m;

    /// <summary>
    /// VAT rate per SAP tax code (OVTG.Code), for the lines that are not standard-rated.
    /// </summary>
    /// <remarks>
    /// Zero-rated and exempt goods are not a rounding detail here: a basket is charged, and a receipt
    /// is declared to ZIMRA, line by line. Applying the standard rate across the whole basket
    /// overcharges the customer on an exempt item and misstates the receipt.
    ///
    /// Keep in step with <c>Fiscalisation:TaxIdMappings</c>, which maps the same codes to FDMS tax
    /// ids — the rate charged locally and the tax id declared to ZIMRA have to describe the same
    /// thing. As shipped: <c>O01</c> is 15.5% Output VAT USD, <c>O8</c> is 15.5% Output VAT ZiG, and
    /// <c>O0</c> is zero-rated.
    /// </remarks>
    public Dictionary<string, decimal> RatesByTaxCode { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The VAT rate for one line's tax code.
    /// </summary>
    public decimal RateFor(string? taxCode)
    {
        if (!string.IsNullOrWhiteSpace(taxCode)
            && RatesByTaxCode.TryGetValue(taxCode.Trim(), out var rate))
        {
            return rate;
        }

        return VatRate;
    }

    /// <summary>
    /// The VAT on a net amount at the rate for that line's tax code.
    /// </summary>
    public decimal VatOn(decimal netAmount, string? taxCode)
        => Math.Round(netAmount * RateFor(taxCode), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The VAT on a basket: each line at its own code's rate, with the net summed per rate and each
    /// rate's VAT rounded once.
    /// </summary>
    /// <remarks>
    /// Once per rate, not once per line, because that is how the receipt is filed. FDMS works each tax
    /// group's VAT back out of the group's tax-inclusive total and rounds it once, so rounding every line
    /// first drifts from the filed figure by up to half a cent a line — and the drift is in the total the
    /// customer is charged. Sale 5 at Graniteside on 11 September, 17, 20 and 20 units at net 6.25, was
    /// totalled 411.48 with VAT of 55.23 that way. The till charged 411.47, SAP invoiced 411.47 with VAT
    /// of 55.22, and ZIMRA holds VAT of 55.22 on a declared cash payment of 411.48 — a cent the customer
    /// never paid.
    ///
    /// Grouped on the rate rather than the code. A line with no code and an O01 line are both
    /// standard-rated and both declared under the default tax id, so FDMS files them as one group. Two
    /// codes at the same rate that map to different tax ids would be one group here and two there, but
    /// the pair that exists, O01 and O8, are the USD and ZiG codes and one sale never carries both.
    /// </remarks>
    public decimal VatOnBasket(IEnumerable<(decimal NetAmount, string? TaxCode)> lines)
        => lines
            .GroupBy(line => RateFor(line.TaxCode))
            .Sum(group => Math.Round(
                group.Sum(line => line.NetAmount) * group.Key, 2, MidpointRounding.AwayFromZero));
}
