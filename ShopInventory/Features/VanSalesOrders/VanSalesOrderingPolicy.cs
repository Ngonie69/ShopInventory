using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesOrders;

/// <inheritdoc />
public sealed class VanSalesOrderingPolicy(
    ApplicationDbContext context,
    ILogger<VanSalesOrderingPolicy> logger) : IVanSalesOrderingPolicy
{
    /// <summary>The <c>SystemConfigs</c> key holding the order cut-off, in hours.</summary>
    public const string CutOffHoursConfigKey = "VanSales.CustomerOrderCutOffHours";

    /// <summary>The <c>SystemConfigs</c> key naming the SAP price list these customers buy on.</summary>
    public const string PriceListConfigKey = "VanSales.CustomerOrderPriceList";

    /// <summary>The <c>SystemConfigs</c> key holding the quantity at or below which stock reads as low.</summary>
    public const string LowStockThresholdConfigKey = "VanSales.CustomerOrderLowStockThreshold";

    /// <summary>
    /// 16:00 the afternoon before, which is when the vans are loaded.
    /// </summary>
    public const int DefaultCutOffHours = 8;

    /// <summary>
    /// Price list 1, which is what the rest of the application already treats as the default —
    /// see <c>SapSqlPriceListExpressions.BuildFallbackPredicate</c> and the merchandiser product
    /// queries, both of which join <c>ITM1</c> on <c>PriceList = 1</c>.
    /// </summary>
    public const int DefaultPriceListNumber = 1;

    /// <summary>Ten units. A band boundary, not a reorder point; it only decides a label.</summary>
    public const decimal DefaultLowStockThreshold = 10m;

    private const int MinCutOffHours = 0;
    private const int MaxCutOffHours = 24 * 7;

    public async Task<VanSalesOrderingRules> GetRulesAsync(CancellationToken cancellationToken)
    {
        // One round trip for all three. The keys are unique-indexed, so this is three index probes
        // rather than a scan.
        var rows = await context.SystemConfigs
            .AsNoTracking()
            .Where(config => config.Key == CutOffHoursConfigKey
                             || config.Key == PriceListConfigKey
                             || config.Key == LowStockThresholdConfigKey)
            .Select(config => new { config.Key, config.Value })
            .ToListAsync(cancellationToken);

        var values = rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);

        return new VanSalesOrderingRules(
            ReadInt(values, CutOffHoursConfigKey, DefaultCutOffHours, MinCutOffHours, MaxCutOffHours),
            ReadInt(values, PriceListConfigKey, DefaultPriceListNumber, min: 1, max: int.MaxValue),
            ReadDecimal(values, LowStockThresholdConfigKey, DefaultLowStockThreshold));
    }

    private int ReadInt(IReadOnlyDictionary<string, string?> values, string key, int fallback, int min, int max)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            logger.LogWarning(
                "{Key} is {Value}, which is not a whole number. Falling back to {Fallback}.",
                key, raw, fallback);
            return fallback;
        }

        if (parsed < min || parsed > max)
        {
            logger.LogWarning(
                "{Key} is {Value}, outside {Min}-{Max}. Falling back to {Fallback}.",
                key, parsed, min, max, fallback);
            return fallback;
        }

        return parsed;
    }

    private decimal ReadDecimal(IReadOnlyDictionary<string, string?> values, string key, decimal fallback)
    {
        if (!values.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0)
        {
            logger.LogWarning(
                "{Key} is {Value}, which is not a non-negative number. Falling back to {Fallback}.",
                key, raw, fallback);
            return fallback;
        }

        return parsed;
    }

    /// <summary>
    /// The config rows as they should be seeded, for a migration or an operator screen to create.
    /// </summary>
    /// <remarks>
    /// Seeded rather than left implicit because an operator cannot edit a row that does not exist.
    /// The descriptions matter as much as the values: "8" on its own does not say eight hours
    /// before what.
    /// </remarks>
    public static IReadOnlyList<SystemConfigEntity> DescribeDefaultRows(DateTime nowUtc) =>
    [
        new()
        {
            Key = CutOffHoursConfigKey,
            Value = DefaultCutOffHours.ToString(CultureInfo.InvariantCulture),
            ValueType = "int",
            Category = "VanSales",
            Description =
                "Hours before midnight (CAT) on a van sales customer's visit day that app ordering closes. "
                + "8 means orders for a Tuesday call must be in by 16:00 on the Monday.",
            IsEditable = true,
            UpdatedAt = nowUtc
        },
        new()
        {
            Key = PriceListConfigKey,
            Value = DefaultPriceListNumber.ToString(CultureInfo.InvariantCulture),
            ValueType = "int",
            Category = "VanSales",
            Description =
                "The SAP price list number van sales customers see in the ordering app. They are all "
                + "on one list. Changing this changes what every customer is quoted.",
            IsEditable = true,
            UpdatedAt = nowUtc
        },
        new()
        {
            Key = LowStockThresholdConfigKey,
            Value = DefaultLowStockThreshold.ToString(CultureInfo.InvariantCulture),
            ValueType = "decimal",
            Category = "VanSales",
            Description =
                "Quantity at or below which an item shows as low stock rather than in stock in the "
                + "customer ordering app. Customers never see the quantity itself, only the band.",
            IsEditable = true,
            UpdatedAt = nowUtc
        }
    ];
}
