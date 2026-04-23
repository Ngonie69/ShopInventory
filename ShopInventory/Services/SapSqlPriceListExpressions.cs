namespace ShopInventory.Services;

internal static class SapSqlPriceListExpressions
{
    public static string BuildFallbackPredicate(string priceListColumn, string customerPriceListColumn, int defaultPriceListNum = 1)
    {
        return $"(({customerPriceListColumn} IS NOT NULL AND {priceListColumn} = {customerPriceListColumn}) OR ({customerPriceListColumn} IS NULL AND {priceListColumn} = {defaultPriceListNum}))";
    }
}