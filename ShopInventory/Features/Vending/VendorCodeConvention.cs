using System.Globalization;

namespace ShopInventory.Features.Vending;

/// <summary>
/// How a vending vendor's code is spelt: the depot's three-letter prefix and a three-digit number,
/// such as <c>VMB001</c>.
/// </summary>
/// <remarks>
/// The prefix belongs to the warehouse the depot draws stock from, not to the depot's business
/// partner, because that is how the business names its vending points: VMB is Bulawayo (KEFBYC), VMP
/// is Graniteside (KEFGRC) and VMM is Machipisa (CORMACH). A code therefore says which depot a vendor
/// buys from on its face, and a code is unique across every depot rather than only within one.
///
/// Van routes' shops share the route customer table and are not held to this; the rule applies only
/// under a business partner a vending account sells on.
/// </remarks>
public static class VendorCodeConvention
{
    public const int Digits = 3;

    public const int MaxNumber = 999;

    public static readonly IReadOnlyDictionary<string, string> PrefixByWarehouse =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["KEFBYC"] = "VMB",
            ["KEFGRC"] = "VMP",
            ["CORMACH"] = "VMM",
        };

    /// <summary>Every prefix with its warehouse, for a sentence: "VMB (KEFBYC), VMP (KEFGRC) and VMM (CORMACH)".</summary>
    public static string Described =>
        string.Join(", ", PrefixByWarehouse.Select(pair => $"{pair.Value} ({pair.Key})"));

    public static string? PrefixForWarehouse(string? warehouseCode) =>
        warehouseCode is not null && PrefixByWarehouse.TryGetValue(warehouseCode.Trim(), out var prefix)
            ? prefix
            : null;

    public static string Format(string prefix, int number) =>
        prefix + number.ToString("D" + Digits, CultureInfo.InvariantCulture);

    public static string? Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    /// <summary>
    /// Reads a code as one of the known prefixes followed by exactly three digits, 001 to 999. Anything
    /// else — an unknown prefix, <c>VMB1</c>, <c>VMB-001</c>, <c>VMB0001</c>, <c>VMB000</c> — is not a
    /// vendor code.
    /// </summary>
    public static bool TryParse(string? code, out string prefix, out int number)
    {
        prefix = string.Empty;
        number = 0;

        var normalized = Normalize(code);
        if (normalized is null)
        {
            return false;
        }

        var knownPrefix = PrefixByWarehouse.Values.FirstOrDefault(candidate =>
            normalized.Length == candidate.Length + Digits &&
            normalized.StartsWith(candidate, StringComparison.Ordinal));

        if (knownPrefix is null)
        {
            return false;
        }

        var digits = normalized[knownPrefix.Length..];
        if (!digits.All(char.IsAsciiDigit) ||
            !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < 1)
        {
            return false;
        }

        prefix = knownPrefix;
        number = parsed;
        return true;
    }

    /// <summary>
    /// The prefix a depot numbers its vendors with, from the warehouses its cashiers draw on — or why it
    /// has none. Exactly one prefix is required: a depot whose cashiers draw on two warehouses with
    /// different prefixes cannot say which one a new vendor belongs to.
    /// </summary>
    public static (string? Prefix, string? Problem) ResolvePrefix(IReadOnlyCollection<string> warehouseCodes)
    {
        if (warehouseCodes.Count == 0)
        {
            return (null, "none of its cashiers has a warehouse assigned, and a vendor's code prefix comes from the warehouse");
        }

        var prefixes = warehouseCodes
            .Select(PrefixForWarehouse)
            .Where(prefix => prefix is not null)
            .Select(prefix => prefix!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (prefixes.Count == 1 && warehouseCodes.All(code => PrefixForWarehouse(code) is not null))
        {
            return (prefixes[0], null);
        }

        if (prefixes.Count > 1)
        {
            return (null, $"its cashiers draw from {string.Join(", ", warehouseCodes)}, which carry different vendor code prefixes ({string.Join(", ", prefixes)})");
        }

        var unmapped = warehouseCodes.Where(code => PrefixForWarehouse(code) is null);
        return (null, $"it draws stock from {string.Join(", ", unmapped)}, which has no vendor code prefix. Prefixes exist for {Described}");
    }
}
