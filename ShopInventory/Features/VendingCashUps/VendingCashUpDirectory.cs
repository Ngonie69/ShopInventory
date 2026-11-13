using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.Vending;

namespace ShopInventory.Features.VendingCashUps;

/// <summary>The names a cash-up is shown under: the cashier, and the depot their warehouse belongs to.</summary>
public sealed class VendingCashUpDirectory
{
    private readonly Dictionary<Guid, (string Username, string Name, string? PartnerCode)> _cashiers;
    private readonly Dictionary<string, string> _partnerByWarehouse;
    private readonly Dictionary<string, string> _partnerNames;

    private VendingCashUpDirectory(
        Dictionary<Guid, (string Username, string Name, string? PartnerCode)> cashiers,
        Dictionary<string, string> partnerByWarehouse,
        Dictionary<string, string> partnerNames)
    {
        _cashiers = cashiers;
        _partnerByWarehouse = partnerByWarehouse;
        _partnerNames = partnerNames;
    }

    public static async Task<VendingCashUpDirectory> LoadAsync(
        ApplicationDbContext context,
        IReadOnlyCollection<Guid> cashierIds,
        CancellationToken cancellationToken)
    {
        var ids = cashierIds.Distinct().ToList();
        var users = ids.Count == 0
            ? []
            : await context.Users
                .AsNoTracking()
                .Where(user => ids.Contains(user.Id))
                .Select(user => new { user.Id, user.Username, user.FirstName, user.LastName, user.AssignedBusinessPartnerCode })
                .ToListAsync(cancellationToken);

        var cashiers = users.ToDictionary(
            user => user.Id,
            user => (user.Username, Name: DisplayName(user.FirstName, user.LastName, user.Username), PartnerCode: user.AssignedBusinessPartnerCode?.Trim()));

        var partnerByWarehouse = await VendingDepots.PartnerByWarehouseAsync(context, cancellationToken);
        var partnerCodes = partnerByWarehouse.Values
            .Concat(cashiers.Values.Select(cashier => cashier.PartnerCode).Where(code => code is not null).Select(code => code!))
            .ToList();
        var partnerNames = await VendingDepots.PartnerNamesAsync(context, partnerCodes, cancellationToken);

        return new VendingCashUpDirectory(cashiers, partnerByWarehouse, partnerNames);
    }

    public bool KnowsCashier(Guid cashierId) => _cashiers.ContainsKey(cashierId);

    public string Username(Guid cashierId, string? fallback = null) =>
        _cashiers.TryGetValue(cashierId, out var cashier) ? cashier.Username : fallback ?? cashierId.ToString();

    public string Name(Guid cashierId, string? fallback = null) =>
        _cashiers.TryGetValue(cashierId, out var cashier) ? cashier.Name : fallback ?? cashierId.ToString();

    /// <summary>The depot a warehouse is the vending point of, else the cashier's own business partner.</summary>
    public string? PartnerCode(string warehouseCode, Guid cashierId) =>
        _partnerByWarehouse.TryGetValue(warehouseCode, out var partner)
            ? partner
            : _cashiers.TryGetValue(cashierId, out var cashier) ? cashier.PartnerCode : null;

    public string? PartnerName(string? partnerCode) =>
        partnerCode is not null && _partnerNames.TryGetValue(partnerCode, out var name) ? name : null;

    public static string DisplayName(string? firstName, string? lastName, string username)
    {
        var name = $"{firstName?.Trim()} {lastName?.Trim()}".Trim();
        return name.Length == 0 ? username : name;
    }
}
