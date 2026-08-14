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
}
