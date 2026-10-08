using System.Text;
using System.Text.RegularExpressions;

namespace ShopInventory.Web.Common;

/// <summary>
/// The shop a business partner's name names, with the currency tag taken off — so the cards SAP keeps
/// for one shop in each currency can be found together.
/// </summary>
/// <remarks>
/// SAP holds a shop once per currency: "Spar Bridge" is SPA002, "SPA050 USD" and SPA070 (ZiG). The
/// currency lands in the code sometimes and in the name sometimes, so the code cannot be relied on;
/// the name, with its currency word and any parenthesised note removed, can. A port of
/// <c>name_key</c> in <c>scripts/DeliveryRoutes/generate_delivery_routes.py</c>, which the delivery
/// routes were built with — keep the two the same.
/// </remarks>
public static partial class BusinessPartnerShopKey
{
    public static string For(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var folded = CurrencyTag().Replace(AsciiFold(name).ToUpperInvariant(), " ");
        folded = folded
            .Replace("&", " AND ", StringComparison.Ordinal)
            .Replace("/", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace(".", " ", StringComparison.Ordinal);
        folded = Parenthesised().Replace(folded, " ");

        return Whitespace().Replace(folded, " ").Trim();
    }

    /// <summary>Accents taken apart and dropped, as Python's NFKD-then-ASCII does.</summary>
    private static string AsciiFold(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (character < 128)
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\b(USD|US\$|FCA|ZIG|RTGS|BOND)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyTag();

    [GeneratedRegex(@"\(.*?\)")]
    private static partial Regex Parenthesised();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
