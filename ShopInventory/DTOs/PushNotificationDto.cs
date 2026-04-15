using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

/// <summary>
/// Request to register a device for push notifications
/// </summary>
public class RegisterDeviceRequest
{
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
    [RegularExpression("^(Android|iOS|Web)$", ErrorMessage = "Platform must be Android, iOS, or Web")]
    public required string Platform { get; set; }

    /// <summary>
    /// Optional device name/model
    /// </summary>
    [MaxLength(200)]
    public string? DeviceName { get; set; }

    /// <summary>
    /// App version string
    /// </summary>
    [MaxLength(50)]
    public string? AppVersion { get; set; }
}

/// <summary>
/// Request to unregister a device
/// </summary>
public class UnregisterDeviceRequest
{
    [Required]
    [MaxLength(512)]
    public required string DeviceToken { get; set; }
}

/// <summary>
/// Response listing a user's registered devices
/// </summary>
public class DeviceRegistrationDto
{
    public int Id { get; set; }
    public string DeviceToken { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? AppVersion { get; set; }
    public DateTime RegisteredAt { get; set; }
    public DateTime? LastActiveAt { get; set; }
}

/// <summary>
/// Request to send a push notification (admin)
/// </summary>
public class SendPushNotificationRequest
{
    /// <summary>
    /// Target username (null = broadcast to all devices)
    /// </summary>
    public string? TargetUsername { get; set; }

    /// <summary>
    /// Target role (null = all roles)
    /// </summary>
    public string? TargetRole { get; set; }

    [Required]
    [MaxLength(200)]
    public required string Title { get; set; }

    [Required]
    [MaxLength(1000)]
    public required string Body { get; set; }

    /// <summary>
    /// Optional key-value data payload for the mobile app
    /// </summary>
    public Dictionary<string, string>? Data { get; set; }
}
