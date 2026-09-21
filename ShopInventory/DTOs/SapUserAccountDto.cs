namespace ShopInventory.DTOs;

/// <summary>
/// One SAP Business One user account as the administration screen shows it: who it is, whether SAP
/// is currently keeping it out, and who last set its password.
/// </summary>
public sealed class SapUserAccountDto
{
    /// <summary>SAP's own key for the account. What the unlock and password routes address.</summary>
    public int InternalKey { get; set; }

    /// <summary>The login code the user types into the B1 client.</summary>
    public string UserCode { get; set; } = string.Empty;

    /// <summary>The person's name as SAP records it.</summary>
    public string? UserName { get; set; }

    public string? Email { get; set; }

    /// <summary>SAP will not let this account sign in until the lock is cleared.</summary>
    public bool IsLocked { get; set; }

    /// <summary>A superuser holds every authorisation in the company.</summary>
    public bool IsSuperuser { get; set; }

    /// <summary>The user code of whoever last set this account's password, as SAP recorded it.</summary>
    public string? LastPasswordChangedBy { get; set; }

    /// <summary>The last sign-out SAP recorded, in SAP's own formatting.</summary>
    public string? LastLogoutDate { get; set; }
}

/// <summary>The accounts, with the count of those currently locked so the screen can lead with it.</summary>
public sealed class SapUserAccountListResponseDto
{
    public List<SapUserAccountDto> Items { get; set; } = [];

    public int TotalCount { get; set; }

    public int LockedCount { get; set; }

    /// <summary>
    /// True when SAP had more accounts than one read returns, so the list is the first page by user
    /// code rather than the whole company.
    /// </summary>
    public bool Truncated { get; set; }
}

/// <summary>What the password route is given. The password is never echoed back.</summary>
public sealed class ChangeSapUserPasswordRequestDto
{
    public string NewPassword { get; set; } = string.Empty;
}
