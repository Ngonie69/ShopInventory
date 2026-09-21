using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Web.Models;

/// <summary>
/// One SAP Business One user account, mirroring the API's <c>SapUserAccountDto</c>.
/// </summary>
/// <remarks>
/// Hand-mirrored, as every model in this folder is, so the nullability has to match the API's
/// exactly: a non-nullable property here against a null the API really sends fails the whole
/// deserialisation, and the page renders as though SAP had no users at all.
/// </remarks>
public class SapUserAccountModel
{
    public int InternalKey { get; set; }

    public string UserCode { get; set; } = string.Empty;

    public string? UserName { get; set; }

    public string? Email { get; set; }

    public bool IsLocked { get; set; }

    public bool IsSuperuser { get; set; }

    public string? LastPasswordChangedBy { get; set; }

    public string? LastLogoutDate { get; set; }

    /// <summary>What the list shows as the person: their name, or the login code when SAP has none.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(UserName) ? UserCode : UserName;
}

/// <summary>The accounts and the two counts the screen leads with.</summary>
public class SapUserAccountListModel
{
    public List<SapUserAccountModel> Items { get; set; } = new();

    public int TotalCount { get; set; }

    public int LockedCount { get; set; }

    /// <summary>SAP had more accounts than one read returns, so the counts describe this page only.</summary>
    public bool Truncated { get; set; }
}

/// <summary>What the change-password dialog collects.</summary>
public class ChangeSapUserPasswordModel
{
    /// <summary>
    /// Bounded here the way the API bounds it. SAP's own policy is stricter on most companies and is
    /// the authority; this only keeps a round trip from being spent on something obviously wrong.
    /// </summary>
    [Required(ErrorMessage = "A new password is required.")]
    [MinLength(8, ErrorMessage = "A SAP password must be at least 8 characters.")]
    [MaxLength(32, ErrorMessage = "A SAP password cannot be longer than 32 characters.")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Type the password again to confirm it.")]
    [Compare(nameof(NewPassword), ErrorMessage = "The two passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
