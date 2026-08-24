using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A one-time code issued to a van sales customer's phone, and the record of what became of it.
/// </summary>
/// <remarks>
/// Keyed on the phone rather than on an account id on purpose: the request endpoint must answer
/// identically whether or not the number is registered, so it cannot look an account up before
/// deciding what to write. An unregistered number simply never gets a row — and never gets a
/// message — while the caller sees the same 202 either way.
/// <para>
/// The code itself is never stored. <see cref="CodeHash"/> is a keyed HMAC, not a bare digest: a
/// six-digit code has a million possibilities, which is nothing to a machine holding a leaked table
/// of plain SHA-256 hashes. Without the key the hash is not worth attacking.
/// </para>
/// </remarks>
[Index(nameof(PhoneE164), nameof(ExpiresAt))]
public class VanSalesCustomerOtpEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(20)]
    public string PhoneE164 { get; set; } = null!;

    /// <summary>HMAC-SHA256 of the code, keyed with the application secret. Never the code itself.</summary>
    [Required]
    [MaxLength(128)]
    public string CodeHash { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    /// <summary>Set the moment the code is accepted. A consumed code is spent, not merely stale.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>Verification attempts against this code, capped so a code cannot be guessed at leisure.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Where the request came from, for abuse review.</summary>
    [MaxLength(64)]
    public string? RequestedFromIp { get; set; }

    /// <summary>Which channel actually carried the code — WhatsApp, SMS, or nothing.</summary>
    [MaxLength(20)]
    public string? DeliveryChannel { get; set; }

    [NotMapped]
    public bool IsConsumed => ConsumedAt.HasValue;

    [NotMapped]
    public bool IsExpired => ExpiresAt <= DateTime.UtcNow;

    /// <summary>Whether this code is still capable of authenticating anyone.</summary>
    [NotMapped]
    public bool IsUsable => !IsConsumed && !IsExpired;
}
