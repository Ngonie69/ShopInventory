using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Common.Validation;

public static class UomQuantityValidation
{
    public static bool AllowDecimalQuantity(string? uomCode)
        => string.Equals(uomCode?.Trim(), "KG", StringComparison.OrdinalIgnoreCase);

    public static async Task<List<string>> ValidateAndNormalizeLineQuantitiesAsync<TLine>(
        ApplicationDbContext context,
        IEnumerable<TLine>? lines,
        Func<TLine, string?> itemCodeSelector,
        Func<TLine, decimal> quantitySelector,
        Func<TLine, string?> uomCodeSelector,
        Action<TLine, string> uomCodeSetter,
        CancellationToken cancellationToken,
        bool requireAtLeastOneLine = true)
    {
        var validationErrors = new List<string>();
        var lineList = lines?.ToList() ?? new List<TLine>();

        if (requireAtLeastOneLine && lineList.Count == 0)
        {
            validationErrors.Add("At least one line item is required");
            return validationErrors;
        }

        if (lineList.Count == 0)
            return validationErrors;

        var lineUomLookup = await ResolveUomLookupAsync(
            context,
            lineList.Select(line => (itemCodeSelector(line), uomCodeSelector(line))),
            cancellationToken);

        for (var index = 0; index < lineList.Count; index++)
        {
            var line = lineList[index];
            var itemCode = itemCodeSelector(line);
            var resolvedUomCode = ResolveLineUomCode(uomCodeSelector(line), itemCode, lineUomLookup);

            if (string.IsNullOrWhiteSpace(uomCodeSelector(line)) && !string.IsNullOrWhiteSpace(resolvedUomCode))
            {
                uomCodeSetter(line, resolvedUomCode);
            }

            var quantity = quantitySelector(line);
            if (quantity <= 0)
            {
                validationErrors.Add(
                    $"Line {index + 1} (Item: {itemCode ?? "unknown"}): Quantity must be greater than zero. Current value: {quantity}");
                continue;
            }

            var quantityError = BuildFractionalQuantityValidationError(index + 1, itemCode, quantity, resolvedUomCode);
            if (!string.IsNullOrWhiteSpace(quantityError))
            {
                validationErrors.Add(quantityError);
            }
        }

        return validationErrors;
    }

    public static bool HasFractionalQuantity(decimal quantity)
        => quantity != decimal.Truncate(quantity);

    public static async Task<Dictionary<string, string>> ResolveUomLookupAsync(
        ApplicationDbContext context,
        IEnumerable<(string? ItemCode, string? UoMCode)> lines,
        CancellationToken cancellationToken)
    {
        var itemCodes = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode) && string.IsNullOrWhiteSpace(line.UoMCode))
            .Select(line => line.ItemCode!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (itemCodes.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var productUoms = await context.Products
            .AsNoTracking()
            .Where(product => itemCodes.Contains(product.ItemCode))
            .Select(product => new
            {
                product.ItemCode,
                UoMCode = product.SalesUnit ?? product.InventoryUOM
            })
            .ToListAsync(cancellationToken);

        var lookup = productUoms
            .Where(product => !string.IsNullOrWhiteSpace(product.UoMCode))
            .GroupBy(product => product.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().UoMCode!, StringComparer.OrdinalIgnoreCase);

        var missingItemCodes = itemCodes
            .Where(itemCode => !lookup.ContainsKey(itemCode))
            .ToList();

        if (missingItemCodes.Count == 0)
            return lookup;

        var merchandiserProductUoms = await context.MerchandiserProducts
            .AsNoTracking()
            .Where(product => product.IsActive && missingItemCodes.Contains(product.ItemCode))
            .Select(product => new
            {
                product.ItemCode,
                product.UoM
            })
            .ToListAsync(cancellationToken);

        foreach (var product in merchandiserProductUoms)
        {
            if (!string.IsNullOrWhiteSpace(product.UoM) && !lookup.ContainsKey(product.ItemCode))
            {
                lookup[product.ItemCode] = product.UoM;
            }
        }

        return lookup;
    }

    public static string? ResolveLineUomCode(
        string? lineUoMCode,
        string? itemCode,
        IReadOnlyDictionary<string, string> lineUomLookup)
    {
        if (!string.IsNullOrWhiteSpace(lineUoMCode))
            return lineUoMCode.Trim();

        if (!string.IsNullOrWhiteSpace(itemCode) && lineUomLookup.TryGetValue(itemCode.Trim(), out var resolvedUomCode))
            return resolvedUomCode;

        return null;
    }

    public static string? BuildFractionalQuantityValidationError(int lineNumber, string? itemCode, decimal quantity, string? resolvedUomCode)
    {
        if (!HasFractionalQuantity(quantity))
            return null;

        if (string.IsNullOrWhiteSpace(resolvedUomCode))
        {
            return $"Line {lineNumber} (Item: {itemCode ?? "unknown"}): Fractional quantity {quantity} requires a unit of measure. Send UoMCode from the client or sync the product UoM before continuing.";
        }

        if (AllowDecimalQuantity(resolvedUomCode))
            return null;

        return $"Line {lineNumber} (Item: {itemCode ?? "unknown"}): Quantity {quantity} is not valid for unit '{resolvedUomCode}'. Fractional quantities are only allowed for KG items.";
    }
}