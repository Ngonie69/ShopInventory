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
/// bound as <c>:prefix</c> through <see cref="SqlItemCodePrefixQuery"/>, the price list as
/// <c>:priceList</c>, and the category and exact code set are filtered in memory.
///
/// <para>
/// Item details and prices are two statements, not one join. The joined form — OITM LEFT JOIN ITM1
/// (and OCRD for a customer's list) under <c>ItemCode LIKE :prefix</c> — was accepted by SAP but took
/// 46–93 seconds per item family, three rounds running, on KEFALOS_TEST_3 (2026-09-18), while each
/// table read alone under the same bound prefix answers in about a second or less. The customer's
/// price list comes from the business partner record rather than a join to OCRD.
/// </para>
///
/// Rule: one statement per code. The unit tests collect code and text from every handler that
/// uses these and fail if a code is ever seen with two texts.
/// </remarks>
internal static class MerchandiserItemSql
{
    /// <summary>
    /// Item master fields the merchandiser handlers use, under SAP's own column names. Shared by all
    /// seven handlers, which previously asked for overlapping columns in different ways.
    /// </summary>
    public const string ItemDetailsCode = "MERCH_ITEM_DETAILS";

    public const string ItemDetailsSql = """
SELECT T0."ItemCode", T0."ItemName", T0."CodeBars", T0."SalUnitMsr", T0."InvntryUom", T0."U_ItemGroup"
FROM OITM T0
WHERE T0."ItemCode" LIKE :prefix
ORDER BY T0."ItemCode"
""";

    /// <summary>
    /// One price list's prices for an item family. Binds <c>:prefix</c> and <c>:priceList</c>.
    /// </summary>
    public const string ItemPricesCode = "MERCH_ITEM_PRICES";

    public const string PriceListParameter = "priceList";

    public const string ItemPricesSql = """
SELECT T1."ItemCode", T1."Price"
FROM ITM1 T1
WHERE T1."ItemCode" LIKE :prefix AND T1."PriceList" = :priceList
ORDER BY T1."ItemCode"
""";

    /// <summary>The list priced when there is no customer, or the customer names none.</summary>
    public const int DefaultPriceList = 1;

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
    /// Rows carry the column names the old joined statement aliased (<c>BarCode</c>, <c>UoM</c>,
    /// <c>InventoryUOM</c>, <c>Category</c>, <c>Price</c>), so the handlers read them unchanged. An
    /// item with no row in the price list gets a null price, as the LEFT JOIN gave it. The price list
    /// is the customer's own when it has one, and <see cref="DefaultPriceList"/> when there is no
    /// customer, the customer does not exist, or it names no list — the same fallback the joined
    /// statement's predicate expressed. The category used to be <c>AND T0."U_ItemGroup" = '…'</c>
    /// in the text: an exact, case-sensitive match on the value as given, which the ordinal
    /// comparison here keeps.
    /// </remarks>
    public static async Task<List<Dictionary<string, object?>>> GetPricedProductsAsync(
        ISAPServiceLayerClient sapClient,
        IEnumerable<string?> itemCodes,
        string? cardCode,
        string? category,
        CancellationToken cancellationToken)
    {
        var codes = itemCodes.ToList();
        var priceList = await ResolvePriceListAsync(sapClient, cardCode, cancellationToken);

        var details = await GetItemDetailsAsync(sapClient, codes, cancellationToken);
        if (details.Count == 0)
        {
            return [];
        }

        var priceRows = await sapClient.ExecuteForItemCodesAsync(
            ItemPricesCode,
            "Merchandiser item prices",
            ItemPricesSql,
            codes,
            cancellationToken,
            new Dictionary<string, string>
            {
                [PriceListParameter] = priceList.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

        var prices = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var row in priceRows)
        {
            if (row.GetValueOrDefault("ItemCode")?.ToString() is { } code)
            {
                prices[code] = row.GetValueOrDefault("Price");
            }
        }

        IEnumerable<Dictionary<string, object?>> products = details.Select(row =>
        {
            var code = row.GetValueOrDefault("ItemCode")?.ToString();
            return new Dictionary<string, object?>
            {
                ["ItemCode"] = code,
                ["ItemName"] = row.GetValueOrDefault("ItemName"),
                ["BarCode"] = row.GetValueOrDefault("CodeBars"),
                ["UoM"] = row.GetValueOrDefault("SalUnitMsr"),
                ["InventoryUOM"] = row.GetValueOrDefault("InvntryUom"),
                ["Category"] = row.GetValueOrDefault("U_ItemGroup"),
                ["Price"] = code is not null ? prices.GetValueOrDefault(code) : null
            };
        });

        if (!string.IsNullOrWhiteSpace(category))
        {
            products = products.Where(row => string.Equals(
                row.GetValueOrDefault("Category")?.ToString(),
                category,
                StringComparison.Ordinal));
        }

        return OrderByItemName(products);
    }

    /// <summary>
    /// The customer's price list, or <see cref="DefaultPriceList"/> when there is no customer, SAP
    /// has no such customer, or the customer names no list.
    /// </summary>
    private static async Task<int> ResolvePriceListAsync(
        ISAPServiceLayerClient sapClient,
        string? cardCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(cardCode))
        {
            return DefaultPriceList;
        }

        var partner = await sapClient.GetBusinessPartnerByCodeAsync(cardCode, cancellationToken);
        return partner?.PriceListNum ?? DefaultPriceList;
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
