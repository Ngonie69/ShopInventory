namespace ShopInventory.Configuration;

/// <summary>
/// Firebase Cloud Messaging configuration
/// </summary>
public class FirebaseSettings
{
    /// <summary>
    /// Enable/disable push notifications
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Path to the Firebase service account JSON key file.
    /// If not set, falls back to GOOGLE_APPLICATION_CREDENTIALS env var.
    /// </summary>
    public string? ServiceAccountKeyPath { get; set; }

    /// <summary>
    /// Firebase project ID (used for FCM endpoint).
    /// If not set, it's read from the service account JSON.
    /// </summary>
    public string? ProjectId { get; set; }
}
