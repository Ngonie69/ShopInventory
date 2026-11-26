using ShopInventory.DTOs;

namespace ShopInventory.Common.Pricing;

/// <summary>
/// The unit price to post on a document line that the price catalogue has priced.
/// </summary>
/// <remarks>
/// <para>
/// A special price reaches the document screens as SAP shows it: the price list price in Unit Price
/// and the gap in Discount % (0.41 less 23.978537% for MUE005 on list 46). The catalogue's
/// <see cref="ItemPriceByListDto.Price"/> is the net price, 0.311688. Writing that net price into a
/// line that still carries the 23.978537% took the discount off twice, and SAP would have billed
/// 0.237 for a 0.311688 item.
/// </para>
/// <para>
/// So a line that carries a discount is priced from the list price when the catalogue has one to
/// give, and its own discount brings it down — the same numbers the screen showed. A line without a
/// discount keeps the net price, so a caller that sends none still gets the special price.
/// </para>
/// </remarks>
public static class DocumentLineUnitPrice
{
    public static decimal For(ItemPriceByListDto cataloguePrice, decimal? lineDiscountPercent)
        => lineDiscountPercent is > 0
           && cataloguePrice.DiscountPercent is > 0
           && cataloguePrice.PriceBeforeDiscount is > 0
            ? cataloguePrice.PriceBeforeDiscount.Value
            : cataloguePrice.Price;
}
