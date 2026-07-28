namespace ShopInventory.Common.Pricing;

/// <summary>
/// Restores the "list price + discount %" shape SAP shows on a document line from a business
/// partner special price. Merging a special price over a price list price collapses both numbers
/// into a single net price, which is why our documents showed 0% where SAP showed the discount.
/// </summary>
public static class SpecialPriceDiscount
{
    /// <summary>
    /// SAP keeps line discounts to six decimals, so a 0.70 list price against a 0.66 special price
    /// reads as 5.714286%. Match that scale or our lines disagree with SAP's on the same document.
    /// </summary>
    private const int DiscountScale = 6;

    /// <summary>
    /// Returns the discount percent that takes <paramref name="listPrice"/> down to
    /// <paramref name="specialPrice"/>, or null when the special price is not a discount
    /// (no list price to discount from, or the special price is not cheaper).
    /// </summary>
    /// <remarks>
    /// A special price of zero means missing data rather than free stock, so it is left alone
    /// instead of being reported as a 100% discount.
    /// </remarks>
    public static decimal? CalculatePercent(decimal listPrice, decimal specialPrice)
    {
        if (listPrice <= 0 || specialPrice <= 0 || specialPrice >= listPrice)
        {
            return null;
        }

        return Math.Round(
            (listPrice - specialPrice) / listPrice * 100m,
            DiscountScale,
            MidpointRounding.AwayFromZero);
    }
}
