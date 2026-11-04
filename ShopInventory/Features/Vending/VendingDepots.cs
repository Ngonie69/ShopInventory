using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;

namespace ShopInventory.Features.Vending;

/// <summary>
/// A vending depot as far as numbering its vendors goes: its business partner, the warehouses its
/// cashiers draw on, and the code prefix those give it — or why they give it none.
/// </summary>
public sealed record VendingDepotCodeRule(
    string BusinessPartnerCode,
    IReadOnlyList<string> WarehouseCodes,
    string? Prefix,
    string? Problem);

/// <summary>
/// Finds vending depots and the codes already issued under <see cref="VendorCodeConvention"/>.
/// </summary>
/// <remarks>
/// A depot is not a table: it is the business partner a <see cref="ApplicationRoles.CartVendor"/>
/// account sells on, the same reading <c>GetVendingOverviewHandler</c> makes. Only active cashiers'
/// warehouses count when there are any, because a closed account still holding last year's warehouse
/// sells nothing and should not stop a depot numbering its vendors.
/// </remarks>
public static class VendingDepots
{
    public static async Task<List<VendingDepotCodeRule>> LoadAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var cashiers = await context.Users
            .AsNoTracking()
            .Where(user => user.Role == ApplicationRoles.CartVendor && user.AssignedBusinessPartnerCode != null)
            .Select(user => new CashierAssignment(user.AssignedBusinessPartnerCode!, user.AssignedWarehouseCodes, user.IsActive))
            .ToListAsync(cancellationToken);

        return cashiers
            .Where(cashier => !string.IsNullOrWhiteSpace(cashier.BusinessPartnerCode))
            .GroupBy(cashier => cashier.BusinessPartnerCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => Rule(group.Key, group.ToList()))
            .ToList();
    }

    /// <summary>The depot on this business partner, or null when no vending account sells on it.</summary>
    public static async Task<VendingDepotCodeRule?> FindAsync(
        ApplicationDbContext context,
        string businessPartnerCode,
        CancellationToken cancellationToken)
    {
        var depots = await LoadAsync(context, cancellationToken);
        return depots.FirstOrDefault(depot =>
            string.Equals(depot.BusinessPartnerCode, businessPartnerCode.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One depot's rule, from the cashiers that sell on its business partner.</summary>
    public static VendingDepotCodeRule Rule(string businessPartnerCode, IReadOnlyCollection<CashierAssignment> cashiers)
    {
        var active = cashiers.Where(cashier => cashier.IsActive).ToList();
        var compared = active.Count > 0 ? active : cashiers.ToList();

        var warehouses = compared
            .SelectMany(cashier => WarehouseCodes(cashier.AssignedWarehouseCodes))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (prefix, problem) = VendorCodeConvention.ResolvePrefix(warehouses);
        return new VendingDepotCodeRule(businessPartnerCode, warehouses, prefix, problem);
    }

    /// <summary>
    /// Each depot warehouse with the business partner it is the depot of: KEFBYC → COR008. A report
    /// names a depot by that partner, because the partner is what the business calls the vending point
    /// and the warehouse is only where its stock sits. A warehouse two depots draw on belongs to
    /// neither, so it is left out and keeps its warehouse label.
    /// </summary>
    public static Dictionary<string, string> PartnerByWarehouse(IEnumerable<VendingDepotCodeRule> depots) =>
        depots
            .SelectMany(depot => depot.WarehouseCodes.Select(warehouse => (Warehouse: warehouse, depot.BusinessPartnerCode)))
            .GroupBy(pair => pair.Warehouse, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(pair => pair.BusinessPartnerCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().BusinessPartnerCode, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc cref="PartnerByWarehouse(IEnumerable{VendingDepotCodeRule})"/>
    public static async Task<Dictionary<string, string>> PartnerByWarehouseAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken) =>
        PartnerByWarehouse(await LoadAsync(context, cancellationToken));

    /// <summary>
    /// The SAP name of each partner, as its latest named till sale carries it (the API keeps no
    /// partner table), so a renamed depot reads as it does now. A partner that has never sold is
    /// missing from the result and callers fall back to its code.
    /// </summary>
    public static async Task<Dictionary<string, string>> PartnerNamesAsync(
        ApplicationDbContext context,
        IEnumerable<string> businessPartnerCodes,
        CancellationToken cancellationToken)
    {
        var codes = businessPartnerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var named = await context.DesktopSales
            .AsNoTracking()
            .Where(sale => codes.Contains(sale.CardCode) && sale.CardName != null && sale.CardName != "")
            .GroupBy(sale => new { sale.CardCode, sale.CardName })
            .Select(group => new { group.Key.CardCode, group.Key.CardName, LastSale = group.Max(sale => sale.Id) })
            .ToListAsync(cancellationToken);

        return named
            .GroupBy(sale => sale.CardCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(sale => sale.LastSale).First().CardName!.Trim(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The highest number issued under each prefix, across every business partner — a code is unique
    /// company-wide, so two depots on the same warehouse draw from one sequence. Removed vendors count:
    /// their codes stay on their sales and are never handed to somebody else.
    /// </summary>
    public static async Task<Dictionary<string, int>> HighestNumbersAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var length = VendorCodeConvention.PrefixByWarehouse.Values.First().Length + VendorCodeConvention.Digits;

        var codes = await context.RouteCustomers
            .AsNoTracking()
            .Where(customer => customer.Code.Length == length)
            .Select(customer => customer.Code)
            .ToListAsync(cancellationToken);

        var highest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            if (VendorCodeConvention.TryParse(code, out var prefix, out var number) &&
                number > highest.GetValueOrDefault(prefix))
            {
                highest[prefix] = number;
            }
        }

        return highest;
    }

    /// <summary>The next unissued code under a prefix, or null when all 999 are taken.</summary>
    public static string? NextCode(string prefix, IReadOnlyDictionary<string, int> highestNumbers)
    {
        var next = highestNumbers.GetValueOrDefault(prefix) + 1;
        return next > VendorCodeConvention.MaxNumber ? null : VendorCodeConvention.Format(prefix, next);
    }

    /// <summary>The same reading as <see cref="User.GetWarehouseCodes"/>, off the projected column.</summary>
    private static IEnumerable<string> WarehouseCodes(string? assignedWarehouseCodes)
    {
        if (string.IsNullOrEmpty(assignedWarehouseCodes))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<string>>(assignedWarehouseCodes) ?? [])
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim());
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public sealed record CashierAssignment(string BusinessPartnerCode, string? AssignedWarehouseCodes, bool IsActive);
}
