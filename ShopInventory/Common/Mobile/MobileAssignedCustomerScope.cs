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
        var customerCodes = Normalize(user.GetCustomerCodes());
        if (customerCodes.Count > 0 || !UsesBlanketMobileScope(user.Role))
        {
            return customerCodes;
        }

        var fallbackCandidates = await db.Users
            .AsNoTracking()
            .Where(candidate => candidate.Id != user.Id &&
                (candidate.Role == "Driver" || candidate.Role == "PodOperator") &&
                candidate.AssignedCustomerCodes != null)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Username,
                candidate.Role,
                candidate.AssignedCustomerCodes
            })
            .ToListAsync(cancellationToken);

        var fallback = fallbackCandidates
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Username,
                candidate.Role,
                Codes = Normalize(Deserialize(candidate.AssignedCustomerCodes))
            })
            .Where(candidate => candidate.Codes.Count > 0)
            .OrderBy(candidate => string.Equals(candidate.Role, "Driver", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(candidate => candidate.Username, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (fallback is null)
        {
            logger.LogWarning(
                "Mobile user {UserId} ({Role}) has no assigned customer codes and no blanket mobile scope source was found",
                user.Id,
                user.Role);

            return customerCodes;
        }

        var serializedCodes = JsonSerializer.Serialize(fallback.Codes);
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
            fallback.Id,
            fallback.Role,
            fallback.Codes.Count);

        return fallback.Codes;
    }

    private static bool UsesBlanketMobileScope(string? role)
        => string.Equals(role, "Driver", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "PodOperator", StringComparison.OrdinalIgnoreCase);

    private static List<string> Deserialize(string? serializedCodes)
    {
        if (string.IsNullOrWhiteSpace(serializedCodes))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(serializedCodes) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static List<string> Normalize(IEnumerable<string>? codes)
    {
        return (codes ?? Enumerable.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}