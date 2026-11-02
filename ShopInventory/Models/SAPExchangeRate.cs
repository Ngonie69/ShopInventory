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

/// <summary>
/// SAP's answer to "what rate is keyed for this currency on this date": a rate, or none keyed.
/// </summary>
/// <remarks>
/// "None keyed" is SAP error -4006, the same refusal a document in that currency dated that day gets.
/// Any other failure is thrown, so a SAP outage is never mistaken for a missing rate.
/// </remarks>
public sealed record SapCurrencyRateLookup(bool IsKeyed, decimal Rate, string? NotKeyedMessage)
{
    public static SapCurrencyRateLookup Found(decimal rate) => new(true, rate, null);

    public static SapCurrencyRateLookup NotKeyed(string message) => new(false, 0m, message);
}

/// <summary>
/// How the SAP company states its exchange rates.
/// </summary>
/// <param name="LocalCurrency">The company's local currency. Production: USD.</param>
/// <param name="SystemCurrency">The system currency. Production: USD.</param>
/// <param name="IndirectQuotation">
/// True when a rate means "1 local = rate foreign" (production: 1 USD = 30 ZiG), false when it means
/// "1 foreign = rate local".
/// </param>
public sealed record SapCurrencyRateSettings(string LocalCurrency, string? SystemCurrency, bool IndirectQuotation)
{
    /// <summary>How many local-currency units one unit of the foreign currency is worth at this SAP rate.</summary>
    public decimal LocalPerForeign(decimal sapRate) =>
        sapRate <= 0 ? 0m : IndirectQuotation ? 1m / sapRate : sapRate;

    /// <summary>How many foreign-currency units one local-currency unit buys at this SAP rate.</summary>
    public decimal ForeignPerLocal(decimal sapRate) =>
        sapRate <= 0 ? 0m : IndirectQuotation ? sapRate : 1m / sapRate;
}

/// <summary>
/// A SAP price list's derivation: its prices are <see cref="Factor"/> × the base list's, unless the
/// list is its own base.
/// </summary>
public sealed record SapPriceListFactor(
    int PriceListNo,
    string Name,
    int BasePriceList,
    decimal Factor,
    string? Currency,
    bool Active)
{
    /// <summary>True when the list's prices are worked out from another list.</summary>
    public bool IsDerived => BasePriceList != PriceListNo;
}
