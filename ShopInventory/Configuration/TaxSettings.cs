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
    public decimal VatRate { get; set; } = 0.155m;
}
