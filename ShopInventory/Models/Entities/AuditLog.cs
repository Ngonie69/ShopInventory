using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Represents an audit log entry for tracking user-initiated events in the API
/// </summary>
public class AuditLog
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The ID of the user who initiated the action
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// The username of the user who initiated the action
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The role of the user at the time of the action
    /// </summary>
    public string UserRole { get; set; } = string.Empty;

    /// <summary>
    /// The type of action performed (e.g., "Login", "CreateInvoice", "ViewPayments")
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// The entity type involved (e.g., "Invoice", "Payment", "Product")
    /// </summary>
    public string? EntityType { get; set; }

    /// <summary>
    /// The ID of the entity involved, if applicable
    /// </summary>
    public string? EntityId { get; set; }

    /// <summary>
    /// Additional details about the action
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// The IP address of the client
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// The user agent string of the client browser
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// The page URL or endpoint that was accessed
    /// </summary>
    public string? PageUrl { get; set; }

    /// <summary>
    /// Whether the action was successful
    /// </summary>
    public bool IsSuccess { get; set; } = true;

    /// <summary>
    /// Error message if the action failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// When the action occurred
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
