using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// Represents an exchange rate from SAP Business One (ORTT table)
/// </summary>
public class SAPExchangeRate
{
    /// <summary>
    /// Currency code (e.g., USD, ZIG)
    /// </summary>
    [JsonPropertyName("Currency")]
    public string Currency { get; set; } = null!;

    /// <summary>
    /// Date of the exchange rate
    /// </summary>
    [JsonPropertyName("RateDate")]
    public DateTime RateDate { get; set; }

    /// <summary>
    /// Exchange rate value (rate against local currency)
    /// </summary>
    [JsonPropertyName("Rate")]
    public decimal Rate { get; set; }
}

/// <summary>
/// Represents a currency defined in SAP Business One (OCRN table)
/// </summary>
public class SAPCurrency
{
    /// <summary>
    /// Currency code (e.g., USD, ZIG, ZAR)
    /// </summary>
    [JsonPropertyName("Code")]
    public string Code { get; set; } = null!;

    /// <summary>
    /// Currency name
    /// </summary>
    [JsonPropertyName("Name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Documents code for currency
    /// </summary>
    [JsonPropertyName("DocumentsCode")]
    public string? DocumentsCode { get; set; }

    /// <summary>
    /// International description
    /// </summary>
    [JsonPropertyName("InternationalDescription")]
    public string? InternationalDescription { get; set; }

    /// <summary>
    /// Decimal places
    /// </summary>
    [JsonPropertyName("Decimals")]
    public string? Decimals { get; set; }

    /// <summary>
    /// Rounding setting
    /// </summary>
    [JsonPropertyName("Rounding")]
    public string? Rounding { get; set; }
}
