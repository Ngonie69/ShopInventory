using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models;

/// <summary>
/// Stored WebAuthn credential for a staff user.
/// </summary>
public class PasskeyCredential
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid UserId { get; set; }

    public User? User { get; set; }

    [Required]
    [MaxLength(512)]
    public string CredentialId { get; set; } = string.Empty;

    [Required]
    public byte[] PublicKey { get; set; } = [];

    [Required]
    public byte[] UserHandle { get; set; } = [];

    public long SignatureCounter { get; set; }

    [MaxLength(128)]
    public string FriendlyName { get; set; } = "Passkey";

    [MaxLength(64)]
    public string? AaGuid { get; set; }

    [MaxLength(255)]
    public string? AttestationFormat { get; set; }

    [MaxLength(256)]
    public string? AuthenticatorTransports { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}