using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.FiscalisationConfiguration;

/// <summary>Turns the nomination row into what the office reads, and names a holder the way a rep would.</summary>
public static class OfflineSigningLeaseMapper
{
    public static FiscalDeviceOfflineLeaseDto ToDto(FiscalDeviceOfflineLeaseEntity nomination) => new()
    {
        DeviceId = nomination.DeviceId,
        HolderUserId = nomination.HolderUserId,
        HolderLabel = nomination.HolderLabel,
        AssignedAtUtc = nomination.AssignedAtUtc,
        AssignedByName = nomination.AssignedByName,
        HolderPendingSales = nomination.HolderPendingSales,
        HolderLastSeenAtUtc = nomination.HolderLastSeenAtUtc,
        CanHandOver = nomination.CanHandOver
    };

    /// <summary>A device nobody has been nominated for. Not stored until someone is.</summary>
    public static FiscalDeviceOfflineLeaseDto Unassigned(int deviceId) => new()
    {
        DeviceId = deviceId,
        CanHandOver = true
    };

    /// <summary>
    /// How to name the holder in a refusal another rep will read.
    /// </summary>
    /// <remarks>
    /// The van's warehouse code first, because that is what a refused rep needs in order to act — "VAN003
    /// is signing offline today" tells the office which van to call, where a person's name may not. The
    /// name is the fallback, and the email the last resort.
    /// </remarks>
    public static string Label(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var van = user.AssignedWarehouseCode?.Trim();
        var name = $"{user.FirstName} {user.LastName}".Trim();

        if (!string.IsNullOrWhiteSpace(van))
        {
            return string.IsNullOrWhiteSpace(name) ? van : $"{van} ({name})";
        }

        return string.IsNullOrWhiteSpace(name)
            ? user.Email ?? "Another handset"
            : name;
    }
}
