using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A customer device's long-lived hold on its session, so a shopkeeper is not made to re-request an
/// OTP every half hour — and so a lost handset can be cut off on its own without disturbing the
/// customer's other phone.
/// </summary>
/// <remarks>
/// Separate from <c>RefreshToken</c>, which has a required foreign key to <see cref="User"/>: these
/// belong to a customer account, not an employee. Same discipline though — the value is stored only
/// as a SHA-256 digest, and rotation replaces the row rather than extending it, so a token seen
/// twice is evidence rather than a convenience.
/// </remarks>
[Index(nameof(TokenHash), IsUnique = true)]
[Index(nameof(VanSalesCustomerAccountId))]
public class VanSalesCustomerRefreshTokenEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public int VanSalesCustomerAccountId { get; set; }

    [ForeignKey(nameof(VanSalesCustomerAccountId))]
    public VanSalesCustomerAccountEntity? Account { get; set; }

    /// <summary>SHA-256 hex of the issued value. The value itself leaves in the response and is never kept.</summary>
    [Required]
    [MaxLength(128)]
    public string TokenHash { get; set; } = null!;

    /// <summary>
    /// The device this token was issued to, so revocation can be per-handset. Supplied by the app
    /// and treated as a label rather than a secret — it identifies, it does not authenticate.
    /// </summary>
    [MaxLength(128)]
    public string? DeviceId { get; set; }

    [MaxLength(200)]
    public string? DeviceName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// The hash that superseded this one. Present means "rotated"; absent on a revoked row means a
    /// deliberate sign-out or an operator cutting the device off, which must not be forgiven.
    /// </summary>
    [MaxLength(128)]
    public string? ReplacedByTokenHash { get; set; }

    [MaxLength(64)]
    public string? CreatedByIp { get; set; }

    [NotMapped]
    public bool IsExpired => ExpiresAt <= DateTime.UtcNow;

    [NotMapped]
    public bool IsRevoked => RevokedAt.HasValue;

    [NotMapped]
    public bool IsActive => !IsRevoked && !IsExpired;
}
