namespace ShopInventory.Web.Data;

/// <summary>
/// Represents an audit log entry for tracking user-initiated events
/// </summary>
public class AuditLog
{
    public int Id { get; set; }

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
    /// The page or endpoint where the action occurred
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
    /// The timestamp when the action occurred
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Common audit action types
/// </summary>
public static class AuditActions
{
    // Authentication
    public const string Login = "Login";
    public const string Logout = "Logout";
    public const string LoginFailed = "LoginFailed";

    // Invoice actions
    public const string ViewInvoices = "ViewInvoices";
    public const string ViewInvoice = "ViewInvoice";
    public const string CreateInvoice = "CreateInvoice";
    public const string UpdateInvoice = "UpdateInvoice";
    public const string DeleteInvoice = "DeleteInvoice";

    // Payment actions
    public const string ViewPayments = "ViewPayments";
    public const string ViewPayment = "ViewPayment";
    public const string CreatePayment = "CreatePayment";

    // Product actions
    public const string ViewProducts = "ViewProducts";
    public const string ViewProduct = "ViewProduct";
    public const string SyncProducts = "SyncProducts";

    // Price actions
    public const string ViewPrices = "ViewPrices";
    public const string SyncPrices = "SyncPrices";

    // Inventory transfer actions
    public const string ViewTransfers = "ViewTransfers";
    public const string ViewTransfer = "ViewTransfer";

    // Settings actions
    public const string ViewSettings = "ViewSettings";
    public const string UpdateSettings = "UpdateSettings";

    // User Management actions
    public const string CreateUser = "CreateUser";
    public const string UpdateUser = "UpdateUser";
    public const string DeleteUser = "DeleteUser";

    // Audit actions
    public const string ViewAuditTrail = "ViewAuditTrail";
    public const string ExportAuditTrail = "ExportAuditTrail";
}
