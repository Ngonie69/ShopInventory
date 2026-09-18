using ShopInventory.Common;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser;

/// <summary>
/// The SAP statements the merchandiser product handlers run, one fixed SqlCode each.
/// </summary>
/// <remarks>
/// These used to interpolate the item codes (and a category, and a card code) into the text under
/// fixed codes shared between handlers — <c>MerchActiveProducts</c> and <c>MerchBackfill</c> each
/// held two different statements — so every call PATCHed the stored query and concurrent callers
/// could run each other's SQL. Now nothing request-specific is in the text: the item-code family is
/// bound as <c>:prefix</c> through <see cref="SqlItemCodePrefixQuery"/>, the card code as
/// <c>:cardCode</c>, and the category and exact code set are filtered in memory.
///
/// Rule: one statement per code. The unit tests collect code and text from every handler that
/// uses these and fail if a code is ever seen with two texts.
/// </remarks>
internal static class MerchandiserItemSql
{
    /// <summary>
    /// Item master fields the backfill and assignment handlers denormalise, under SAP's own column
    /// names. Shared by five handlers, which previously asked for the same columns five ways.
    /// </summary>
    public const string ItemDetailsCode = "MERCH_ITEM_DETAILS";

    public const string ItemDetailsSql = """
SELECT T0."ItemCode", T0."ItemName", T0."CodeBars", T0."SalUnitMsr", T0."U_ItemGroup"
FROM OITM T0
WHERE T0."ItemCode" LIKE :prefix
ORDER BY T0."ItemCode"
""";

    /// <summary>
    /// The mobile product list priced at price list 1: a merchandiser's own list, and the customer
    /// list when no card code is given.
    /// </summary>
    public const string ProductsListOneCode = "MERCH_PRODUCTS_PL1";

    public const string ProductsListOneSql = """
SELECT T0."ItemCode", T0."ItemName", T0."CodeBars" AS "BarCode", T0."SalUnitMsr" AS "UoM", T0."InvntryUom" AS "InventoryUOM", T0."U_ItemGroup" AS "Category", T1."Price"
FROM OITM T0
LEFT JOIN ITM1 T1 ON T0."ItemCode" = T1."ItemCode" AND T1."PriceList" = 1
WHERE T0."ItemCode" LIKE :prefix
ORDER BY T0."ItemCode"
""";

    /// <summary>
    /// The mobile product list priced for one customer: their price list, or list 1 when the
    /// customer has none or does not exist. Binds <c>:cardCode</c>.
    /// </summary>
    public const string ProductsForCustomerCode = "MERCH_PRODUCTS_CUST";

    public const string CardCodeParameter = "cardCode";

    public static readonly string ProductsForCustomerSql = $"""
SELECT T0."ItemCode", T0."ItemName", T0."CodeBars" AS "BarCode", T0."SalUnitMsr" AS "UoM", T0."InvntryUom" AS "InventoryUOM", T0."U_ItemGroup" AS "Category", T1."Price"
FROM OITM T0
LEFT JOIN OCRD T2 ON T2."CardCode" = :cardCode
LEFT JOIN ITM1 T1 ON T0."ItemCode" = T1."ItemCode" AND {SapSqlPriceListExpressions.BuildFallbackPredicate(@"T1.""PriceList""", @"T2.""ListNum""")}
WHERE T0."ItemCode" LIKE :prefix
ORDER BY T0."ItemCode"
""";

    /// <summary>
    /// The <see cref="ItemDetailsSql"/> rows for <paramref name="itemCodes"/>.
    /// </summary>
    public static Task<List<Dictionary<string, object?>>> GetItemDetailsAsync(
        ISAPServiceLayerClient sapClient,
        IEnumerable<string?> itemCodes,
        CancellationToken cancellationToken) =>
        sapClient.ExecuteForItemCodesAsync(
            ItemDetailsCode,
            "Merchandiser item details",
            ItemDetailsSql,
            itemCodes,
            cancellationToken);

    /// <summary>
    /// The priced product rows for <paramref name="itemCodes"/>, optionally for one customer and
    /// one category, in the order the old statement's <c>ORDER BY T0."ItemName"</c> returned.
    /// </summary>
    /// <remarks>
    /// The category used to be <c>AND T0."U_ItemGroup" = '…'</c> in the text: an exact,
    /// case-sensitive match on the value as given, which is what the ordinal comparison here keeps.
    /// </remarks>
    public static async Task<List<Dictionary<string, object?>>> GetPricedProductsAsync(
        ISAPServiceLayerClient sapClient,
        IEnumerable<string?> itemCodes,
        string? cardCode,
        string? category,
        CancellationToken cancellationToken)
    {
        var rows = string.IsNullOrEmpty(cardCode)
            ? await sapClient.ExecuteForItemCodesAsync(
                ProductsListOneCode,
                "Merchandiser products (price list 1)",
                ProductsListOneSql,
                itemCodes,
                cancellationToken)
            : await sapClient.ExecuteForItemCodesAsync(
                ProductsForCustomerCode,
                "Merchandiser products (customer price list)",
                ProductsForCustomerSql,
                itemCodes,
                cancellationToken,
                new Dictionary<string, string> { [CardCodeParameter] = cardCode });

        IEnumerable<Dictionary<string, object?>> filtered = rows;
        if (!string.IsNullOrWhiteSpace(category))
        {
            filtered = filtered.Where(row => string.Equals(
                (row.GetValueOrDefault("Category") ?? row.GetValueOrDefault("U_ItemGroup"))?.ToString(),
                category,
                StringComparison.Ordinal));
        }

        return OrderByItemName(filtered);
    }

    /// <summary>
    /// HANA's <c>ORDER BY "ItemName"</c>: binary string order, nulls first. Ties, which HANA left
    /// in no particular order, break on the item code so the list is stable.
    /// </summary>
    public static List<Dictionary<string, object?>> OrderByItemName(IEnumerable<Dictionary<string, object?>> rows) =>
        rows
            .OrderBy(row => row.GetValueOrDefault("ItemName")?.ToString(), StringComparer.Ordinal)
            .ThenBy(row => row.GetValueOrDefault("ItemCode")?.ToString(), StringComparer.Ordinal)
            .ToList();

    /// <summary>HANA's <c>ORDER BY "ItemCode"</c>: binary string order.</summary>
    public static List<Dictionary<string, object?>> OrderByItemCode(IEnumerable<Dictionary<string, object?>> rows) =>
        rows
            .OrderBy(row => row.GetValueOrDefault("ItemCode")?.ToString(), StringComparer.Ordinal)
            .ToList();
}
