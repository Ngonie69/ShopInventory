namespace ShopInventory.Common.Pricing;

/// <summary>
/// Which of SAP's special price rows apply to a customer, and which one wins.
/// </summary>
/// <remarks>
/// <para>
/// SAP keeps two kinds of special price in the one table (OSPP, the Service Layer's
/// <c>SpecialPrices</c>). A row under the customer's own card code is a special price for that
/// business partner. A row under <c>*</c> followed by a price list number — <c>*46</c> — is a
/// "Period and Volume Discount" on that price list, and applies to every customer who buys on it.
/// SAP prices a line from the customer's own row when one is in force, and from the price list's
/// row otherwise.
/// </para>
/// <para>
/// Reading only the customer's own card code missed every period discount: MUE005 for COR007 on
/// 2026-11-26 came out at the 0.41 list price where SAP charged 0.41 less 23.978537%, from the
/// <c>*46</c> row.
/// </para>
/// </remarks>
public static class SpecialPriceScope
{
    /// <summary>
    /// The card code SAP files a price list's period and volume discounts under, or null when
    /// there is no price list to look for.
    /// </summary>
    public static string? PriceListCardCode(int? priceListNum)
        => priceListNum is > 0 ? $"*{priceListNum.Value}" : null;

    /// <summary>
    /// The card codes whose rows can price a line for this customer, the customer's own first.
    /// </summary>
    public static IReadOnlyList<string> CardCodes(string cardCode, int? priceListNum)
        => PriceListCardCode(priceListNum) is { } priceListCardCode
            ? new[] { cardCode, priceListCardCode }
            : new[] { cardCode };

    /// <summary>
    /// One price per item from rows already known to be in force today: the customer's own row
    /// when there is one, otherwise the price list's.
    /// </summary>
    /// <remarks>
    /// Only rows in force are passed in, so an expired customer row cannot shadow a current
    /// period discount — the customer then pays the period price, as SAP charges it.
    /// </remarks>
    public static Dictionary<string, decimal> Resolve(
        string cardCode,
        IEnumerable<(string CardCode, string ItemCode, decimal Price)> currentRows)
    {
        var customerPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var priceListPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in currentRows)
        {
            if (string.IsNullOrWhiteSpace(row.ItemCode))
            {
                continue;
            }

            var target = string.Equals(row.CardCode?.Trim(), cardCode.Trim(), StringComparison.OrdinalIgnoreCase)
                ? customerPrices
                : priceListPrices;

            target.TryAdd(row.ItemCode.Trim(), row.Price);
        }

        foreach (var customerPrice in customerPrices)
        {
            priceListPrices[customerPrice.Key] = customerPrice.Value;
        }

        return priceListPrices;
    }
}
