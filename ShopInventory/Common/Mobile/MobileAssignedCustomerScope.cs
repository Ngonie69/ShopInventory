using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;

namespace ShopInventory.Common.Mobile;

public static class MobileAssignedCustomerScope
{
    public static async Task<List<string>> GetEffectiveCustomerCodesAsync(
        ApplicationDbContext db,
        User user,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var assignedBusinessPartnerScope = NormalizeAssignedBusinessPartnerScope(user);
        if (assignedBusinessPartnerScope.Count > 0)
        {
            return assignedBusinessPartnerScope;
        }

        var customerCodes = Normalize(user.GetCustomerCodes());
        if (customerCodes.Count > 0 || !UsesBlanketMobileScope(user.Role))
        {
            return customerCodes;
        }

        var fallback = await DriverShopList.FindAsync(db, user.Id, cancellationToken);

        if (fallback is null)
        {
            logger.LogWarning(
                "Mobile user {UserId} ({Role}) has no assigned customer codes and no blanket mobile scope source was found",
                user.Id,
                user.Role);

            return customerCodes;
        }

        var serializedCodes = JsonSerializer.Serialize(fallback.CustomerCodes);
        await db.Users
            .Where(candidate => candidate.Id == user.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.AssignedCustomerCodes, _ => serializedCodes)
                .SetProperty(candidate => candidate.UpdatedAt, _ => DateTime.UtcNow),
                cancellationToken);

        logger.LogInformation(
            "Backfilled assigned customer codes for mobile user {UserId} ({Role}) from {SourceUserId} ({SourceRole}) with {Count} customer(s)",
            user.Id,
            user.Role,
            fallback.UserId,
            fallback.Role,
            fallback.CustomerCodes.Count);

        return fallback.CustomerCodes;
    }

    private static bool UsesBlanketMobileScope(string? role)
        => ApplicationRoles.UsesBlanketMobileScope(role);

    private static List<string> NormalizeAssignedBusinessPartnerScope(User user)
    {
        if (!ApplicationRoles.UsesRouteCustomerScope(user.Role))
        {
            return new List<string>();
        }

        return Normalize(new[] { user.AssignedBusinessPartnerCode ?? string.Empty });
    }

    internal static List<string> Normalize(IEnumerable<string>? codes)
    {
        return (codes ?? Enumerable.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
