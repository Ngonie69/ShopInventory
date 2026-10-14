using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A password a staff account used to have.
/// </summary>
/// <remarks>
/// Kept so that a renewal cannot bring an old password back. Only passwords that have been
/// replaced are here — the one in use stays on <see cref="User.PasswordHash"/> alone — and only the
/// most recent few are kept; see <c>PasswordPolicySettings.RememberedPasswords</c>.
/// </remarks>
public class UserPasswordHistoryEntity
{
    [Key]
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>The BCrypt hash as it stood on the account.</summary>
    [Required]
    [MaxLength(255)]
    public required string PasswordHash { get; set; }

    /// <summary>When it stopped being the account's password.</summary>
    public DateTime ReplacedAt { get; set; } = DateTime.UtcNow;
}
