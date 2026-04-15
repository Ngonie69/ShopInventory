using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models;

/// <summary>
/// Stores FCM device tokens for mobile push notifications
/// </summary>
public class PushDeviceRegistration
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The user this device belongs to
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Firebase Cloud Messaging device token
    /// </summary>
    [Required]
    [MaxLength(512)]
    public required string DeviceToken { get; set; }

    /// <summary>
    /// Platform: Android, iOS, Web
    /// </summary>
    [Required]
    [MaxLength(20)]
    public required string Platform { get; set; }

    /// <summary>
    /// Optional device name/model for display (e.g. "Samsung Galaxy S24")
    /// </summary>
    [MaxLength(200)]
    public string? DeviceName { get; set; }

    /// <summary>
    /// App version at time of registration
    /// </summary>
    [MaxLength(50)]
    public string? AppVersion { get; set; }

    /// <summary>
    /// When the token was registered or last refreshed
    /// </summary>
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When we last successfully sent a push to this token
    /// </summary>
    public DateTime? LastActiveAt { get; set; }

    /// <summary>
    /// If true, the token is known to be invalid (FCM returned NotRegistered/InvalidRegistration)
    /// </summary>
    public bool IsRevoked { get; set; }

    /// <summary>
    /// Navigation property
    /// </summary>
    public User? User { get; set; }
}
