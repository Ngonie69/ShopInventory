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
    bool CanAcceptAny)
{
    public bool Holds(string warehouseCode)
        => Warehouses.Contains(warehouseCode, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the transfer is one this person may see: their warehouse at either end, or every one.</summary>
    public bool MaySee(string source, string destination)
        => CanAcceptAny || Holds(source) || Holds(destination);

    /// <summary>
    /// Whether this person may accept or reject a transfer into <paramref name="destination"/>. Never the
    /// sender: the point of the handover is that the other side counts it.
    /// </summary>
    public bool MayDecide(FactoryTransferEntity transfer)
        => transfer.SentByUserId != UserId
            && ((CanAccept && Holds(transfer.DestinationWarehouseCode)) || CanAcceptAny);

    public static async Task<FactoryTransferActor?> ResolveAsync(
        ApplicationDbContext context,
        IUserManagementService userManagement,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await context.Users
            .AsNoTracking()
            .Where(account => account.Id == userId && account.IsActive)
            .Select(account => new { account.FirstName, account.LastName, account.Username, account.AssignedWarehouseCodes })
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

        return new FactoryTransferActor(
            userId,
            string.IsNullOrWhiteSpace(fullName) ? user.Username : fullName,
            warehouses,
            CanSend: isSystemAdmin || permissions.Contains(Permission.SendFactoryTransfers),
            CanAccept: isSystemAdmin || permissions.Contains(Permission.AcceptFactoryTransfers),
            CanAcceptAny: isSystemAdmin || permissions.Contains(Permission.AcceptAnyFactoryTransfers));
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
