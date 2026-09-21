using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.SapUsers;

/// <summary>
/// Turns what SAP sends into what the administration screen shows. One place rather than one per
/// handler: the list and the two writes all answer with the same row, and a screen that showed a
/// different shape after an unlock than before it would read as the unlock having changed something
/// else as well.
/// </summary>
internal static class SapUserAccountMapping
{
    public static SapUserAccountDto ToDto(SAPUserAccount account) => new()
    {
        InternalKey = account.InternalKey,
        UserCode = account.UserCode ?? string.Empty,
        UserName = account.UserName,
        Email = account.EMail,
        IsLocked = account.IsLocked,
        IsSuperuser = account.IsSuperuser,
        LastPasswordChangedBy = account.LastPasswordChangedBy,
        LastLogoutDate = account.LastLogoutDate
    };
}
