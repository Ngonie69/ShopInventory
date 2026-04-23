namespace ShopInventory.Web;

public static class QuantityDisplay
{
    public static string Format(decimal quantity, string? uomCode = null)
    {
        if (IsKilogram(uomCode))
            return quantity.ToString("#,##0.0###");

        return quantity == decimal.Truncate(quantity)
            ? quantity.ToString("#,##0")
            : quantity.ToString("#,##0.####");
    }

    public static string FormatWithUom(decimal quantity, string? uomCode)
    {
        var formattedQuantity = Format(quantity, uomCode);
        return string.IsNullOrWhiteSpace(uomCode)
            ? formattedQuantity
            : $"{formattedQuantity} {uomCode}";
    }

    private static bool IsKilogram(string? uomCode)
        => string.Equals(uomCode?.Trim(), "KG", StringComparison.OrdinalIgnoreCase);
}