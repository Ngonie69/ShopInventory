using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.FactoryTransfers;

/// <summary>
/// Who is acting on a factory transfer: their name, the warehouses on their account and what they
/// may do there.
/// </summary>
/// <remarks>
/// The name is read from the account rather than <c>User.Identity.Name</c>: the Web calls with its
/// integration key alongside the user's token, and the key's identity comes first.
/// </remarks>
public sealed record FactoryTransferActor(
    Guid UserId,
    string Name,
    IReadOnlyList<string> Warehouses,
    bool CanSend,
    bool CanAccept,
    bool CanAcceptAny,
    bool CanDeliver = false)
{
    public bool Holds(string warehouseCode)
        => Warehouses.Contains(warehouseCode, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the transfer is one this person may see: their warehouse at either end, or every one.</summary>
    public bool MaySee(string source, string destination)
        => CanAcceptAny || Holds(source) || Holds(destination);

    /// <summary>
    /// Whether this person may accept or reject the transfer. Never the sender: the point of the
    /// handover is that the other side counts it.
    /// </summary>
    public bool MayDecide(FactoryTransferEntity transfer)
        => transfer.SentByUserId != UserId && MayDecideInto(transfer.Direction, transfer.DestinationWarehouseCode);

    /// <summary>
    /// The receiving side of a transfer into <paramref name="destination"/>. Accepting for any
    /// department is the dispatch manager's reach on the factory floor; it does not reach a depot,
    /// where only the people who run it can say what came off the truck.
    /// </summary>
    public bool MayDecideInto(string direction, string destination)
        => (CanAccept && Holds(destination))
            || (CanAcceptAny && direction != FactoryTransferDirections.Delivery);

    public static async Task<FactoryTransferActor?> ResolveAsync(
        ApplicationDbContext context,
        IUserManagementService userManagement,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await context.Users
            .AsNoTracking()
            .Where(account => account.Id == userId && account.IsActive)
            .Select(account => new { account.FirstName, account.LastName, account.Username, account.AssignedWarehouseCodes, account.Role })
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null)
            return null;

        var permissions = await userManagement.GetEffectivePermissionsAsync(userId);
        var isSystemAdmin = permissions.Contains(Permission.SystemAdmin);

        var fullName = string.Join(" ", new[] { user.FirstName, user.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

        var warehouses = ReadWarehouses(user.AssignedWarehouseCodes)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToList();

        // The wash bay runs one warehouse and its role says so, so its accounts need no assignment for it.
        if (string.Equals(user.Role, ApplicationRoles.WashBay, StringComparison.OrdinalIgnoreCase)
            && !warehouses.Contains(FactoryDepartments.WashbayWarehouseCode, StringComparer.OrdinalIgnoreCase))
            warehouses.Add(FactoryDepartments.WashbayWarehouseCode);

        return new FactoryTransferActor(
            userId,
            string.IsNullOrWhiteSpace(fullName) ? user.Username : fullName,
            warehouses,
            CanSend: isSystemAdmin || permissions.Contains(Permission.SendFactoryTransfers),
            CanAccept: isSystemAdmin || permissions.Contains(Permission.AcceptFactoryTransfers),
            CanAcceptAny: isSystemAdmin || permissions.Contains(Permission.AcceptAnyFactoryTransfers),
            CanDeliver: isSystemAdmin || permissions.Contains(Permission.DeliverFactoryTransfers));
    }

    /// <summary>The account's warehouse list, read as <see cref="User.GetWarehouseCodes"/> reads it.</summary>
    private static List<string> ReadWarehouses(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
