using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Common.Validation;

public static class UomQuantityValidation
{
    /// <summary>
    /// How far a batch or serial selection may be from the line quantity before it is wrong.
    /// </summary>
    public const decimal AllocationQuantityTolerance = 0.000001m;

    /// <summary>
    /// Reports a batch or serial selection that does not account for the whole line quantity.
    /// </summary>
    /// <remarks>
    /// SAP answers such a line with -4014 — "Cannot add row without complete selection of
    /// batch/serial numbers" — which names neither the line nor the item, so the documents that
    /// carry their own selection are checked before they are sent.
    ///
    /// <para>
    /// Lives here rather than beside the SAP client so a handler can apply it while it still has
    /// somewhere useful to put the answer. Reaching SAP was the only thing that used to enforce it,
    /// which meant a caller learned about a mis-selected batch as an <c>ArgumentException</c> thrown
    /// from inside the posting client rather than as a validation failure naming the line.
    /// </para>
    ///
    /// <para>
    /// Pure and synchronous on purpose: it reads nothing, so every caller can run it, including the
    /// SAP client, which has no database.
    /// </para>
    /// </remarks>
    /// <param name="lineIndex">Zero-based; messages report it one-based.</param>
    /// <param name="itemCode">The line's item, for the message.</param>
    /// <param name="quantity">The line quantity the selection has to add up to.</param>
    /// <param name="batches">The batch selection, if the line carries one.</param>
    /// <param name="serialNumbers">The serial selection, if the line carries one.</param>
    public static IEnumerable<string> DescribeLineSelectionProblems(
        int lineIndex,
        string? itemCode,
        decimal quantity,
        IEnumerable<(string? BatchNumber, decimal Quantity)>? batches,
        IEnumerable<string?>? serialNumbers)
    {
        var batchList = batches?.ToList();
        if (batchList is { Count: > 0 })
        {
            if (batchList.Any(batch => string.IsNullOrWhiteSpace(batch.BatchNumber)))
            {
                yield return $"Line {lineIndex + 1}: Batch number is required for item {itemCode}";
            }

            if (batchList.Any(batch => batch.Quantity <= 0))
            {
                yield return $"Line {lineIndex + 1}: Batch quantities must be greater than zero";
            }

            var selected = batchList.Sum(batch => batch.Quantity);
            if (Math.Abs(selected - quantity) > AllocationQuantityTolerance)
            {
                yield return
                    $"Line {lineIndex + 1}: the batch selection for item {itemCode} covers {selected} of {quantity}. " +
                    $"SAP requires the batch quantities on a line to add up to the line quantity.";
            }
        }

        var serialList = serialNumbers?.ToList();
        if (serialList is { Count: > 0 })
        {
            if (serialList.Any(string.IsNullOrWhiteSpace))
            {
                yield return $"Line {lineIndex + 1}: Serial number is required for item {itemCode}";
            }

            var duplicate = serialList
                .Where(serial => !string.IsNullOrWhiteSpace(serial))
                .GroupBy(serial => serial, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                yield return
                    $"Line {lineIndex + 1}: serial number '{duplicate.Key}' is listed more than once for item {itemCode}";
            }

            if (quantity != Math.Truncate(quantity))
            {
                yield return
                    $"Line {lineIndex + 1}: item {itemCode} is serial-managed, so its quantity must be a whole " +
                    $"number of units. Current value: {quantity}";
            }
            else if (serialList.Count != (int)quantity)
            {
                yield return
                    $"Line {lineIndex + 1}: the serial selection for item {itemCode} covers {serialList.Count} of " +
                    $"{quantity} units. SAP requires one serial number per unit.";
            }
        }
    }

    public static string? NormalizeItemCode(string? itemCode)
        => string.IsNullOrWhiteSpace(itemCode)
            ? null
            : itemCode.Trim().ToUpperInvariant();

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
            .Select(line => NormalizeItemCode(line.ItemCode)!)
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
