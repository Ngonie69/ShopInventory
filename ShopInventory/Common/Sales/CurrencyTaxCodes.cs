namespace ShopInventory.Common.Sales;

/// <summary>
/// The output VAT group a line is charged under in the currency of the document it is on.
/// </summary>
/// <remarks>
/// SAP's item master records one VAT group per item, and for most of the catalogue — butter, cheese,
/// cream, yoghurt, ice cream — that group is <c>O1</c>, 15.5% output VAT for ZiG. Copied straight onto a
/// USD sale it posts USD tax to the ZiG VAT control account. The rate is the same 15.5% either way, so
/// nothing refuses it and no total changes: the VAT simply lands in the wrong ledger. On a USD document
/// the group is <c>O01</c>, 15.5% output VAT for USD.
///
/// <para>
/// Only the substitution the business has asked for. Exempt and zero-rated groups are the same in every
/// currency and pass through untouched, as does every code on a document in any other currency.
/// </para>
/// </remarks>
public static class CurrencyTaxCodes
{
    /// <summary>Standard-rated output VAT, ZiG.</summary>
    public const string ZigStandard = "O1";

    /// <summary>Standard-rated output VAT, USD.</summary>
    public const string UsdStandard = "O01";

    /// <summary>
    /// Whether a document in this currency is a USD one. Blank counts: SAP posts a document that names
    /// no currency in the company's local currency, which is USD.
    /// </summary>
    public static bool IsUsd(string? currency)
        => string.IsNullOrWhiteSpace(currency)
           || string.Equals(currency.Trim(), "USD", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The tax code to charge on a document in <paramref name="currency"/>: <c>O01</c> in place of
    /// <c>O1</c> on a USD document, and the code unchanged everywhere else.
    /// </summary>
    public static string? ForCurrency(string? taxCode, string? currency)
        => IsUsd(currency) && string.Equals(taxCode?.Trim(), ZigStandard, StringComparison.OrdinalIgnoreCase)
            ? UsdStandard
            : taxCode;
}
