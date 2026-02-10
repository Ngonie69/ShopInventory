using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Entity for storing exchange rates
/// </summary>
public class ExchangeRateEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Source currency code (e.g., USD)
    /// </summary>
    [Required]
    [MaxLength(10)]
    public string FromCurrency { get; set; } = null!;

    /// <summary>
    /// Target currency code (e.g., ZIG)
    /// </summary>
    [Required]
    [MaxLength(10)]
    public string ToCurrency { get; set; } = null!;

    /// <summary>
    /// Exchange rate (how many ToCurrency for 1 FromCurrency)
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal Rate { get; set; }

    /// <summary>
    /// Inverse rate (how many FromCurrency for 1 ToCurrency)
    /// </summary>
    [Column(TypeName = "decimal(18,6)")]
    public decimal InverseRate { get; set; }

    /// <summary>
    /// Rate effective date
    /// </summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Source of the rate (e.g., "RBZ", "Manual", "API")
    /// </summary>
    [MaxLength(50)]
    public string? Source { get; set; }

    /// <summary>
    /// Whether this is the current active rate for this currency pair
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who created/updated the rate
    /// </summary>
    public Guid? CreatedByUserId { get; set; }

    // Navigation
    public virtual User? CreatedByUser { get; set; }
}

/// <summary>
/// Entity for system configuration settings
/// </summary>
public class SystemConfigEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Configuration key
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Key { get; set; } = null!;

    /// <summary>
    /// Configuration value (JSON for complex values)
    /// </summary>
    [MaxLength(4000)]
    public string? Value { get; set; }

    /// <summary>
    /// Value type (string, int, bool, json, encrypted)
    /// </summary>
    [MaxLength(20)]
    public string ValueType { get; set; } = "string";

    /// <summary>
    /// Configuration category
    /// </summary>
    [MaxLength(50)]
    public string? Category { get; set; }

    /// <summary>
    /// Description
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Whether this setting can be edited by users
    /// </summary>
    public bool IsEditable { get; set; } = true;

    /// <summary>
    /// Whether this setting contains sensitive data
    /// </summary>
    public bool IsSensitive { get; set; } = false;

    /// <summary>
    /// Last modified timestamp
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who last modified
    /// </summary>
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>
/// Entity for database backups
/// </summary>
public class BackupEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Backup file name
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string FileName { get; set; } = null!;

    /// <summary>
    /// Backup file path
    /// </summary>
    [MaxLength(500)]
    public string? FilePath { get; set; }

    /// <summary>
    /// Backup size in bytes
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Backup type (Full, Incremental, Differential)
    /// </summary>
    [MaxLength(20)]
    public string BackupType { get; set; } = "Full";

    /// <summary>
    /// Backup status (InProgress, Completed, Failed)
    /// </summary>
    [MaxLength(20)]
    public string Status { get; set; } = "InProgress";

    /// <summary>
    /// Error message if failed
    /// </summary>
    [MaxLength(1000)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Backup start time
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Backup completion time
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// User who initiated the backup
    /// </summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>
    /// Description/notes
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Whether backup is stored offsite/cloud
    /// </summary>
    public bool IsOffsite { get; set; } = false;

    /// <summary>
    /// Cloud storage URL if offsite
    /// </summary>
    [MaxLength(500)]
    public string? CloudUrl { get; set; }

    // Navigation
    public virtual User? CreatedByUser { get; set; }
}

/// <summary>
/// Entity for API rate limit tracking
/// </summary>
public class ApiRateLimitEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Client identifier (IP, API key, or user ID)
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string ClientId { get; set; } = null!;

    /// <summary>
    /// Client type (IP, ApiKey, User)
    /// </summary>
    [MaxLength(20)]
    public string ClientType { get; set; } = "IP";

    /// <summary>
    /// Endpoint or route pattern
    /// </summary>
    [MaxLength(255)]
    public string? Endpoint { get; set; }

    /// <summary>
    /// Request count in current window
    /// </summary>
    public int RequestCount { get; set; }

    /// <summary>
    /// Window start time
    /// </summary>
    public DateTime WindowStart { get; set; }

    /// <summary>
    /// Window duration in seconds
    /// </summary>
    public int WindowDurationSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum requests allowed in window
    /// </summary>
    public int MaxRequests { get; set; } = 100;

    /// <summary>
    /// Whether client is currently blocked
    /// </summary>
    public bool IsBlocked { get; set; } = false;

    /// <summary>
    /// Block expiry time
    /// </summary>
    public DateTime? BlockExpiresAt { get; set; }

    /// <summary>
    /// Total blocked count
    /// </summary>
    public int TotalBlockedCount { get; set; }

    /// <summary>
    /// Last request time
    /// </summary>
    public DateTime LastRequestAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Entity for user role and permission assignments
/// </summary>
public class UserPermissionEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// User ID
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Permission string (e.g., "invoices.view")
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Permission { get; set; } = null!;

    /// <summary>
    /// Whether permission is granted (true) or denied (false)
    /// </summary>
    public bool IsGranted { get; set; } = true;

    /// <summary>
    /// Who assigned this permission
    /// </summary>
    public Guid? AssignedByUserId { get; set; }

    /// <summary>
    /// When permission was assigned
    /// </summary>
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Permission expiry date (null = permanent)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    // Navigation
    public virtual User User { get; set; } = null!;
    public virtual User? AssignedByUser { get; set; }
}

/// <summary>
/// Entity for role definitions
/// </summary>
public class RoleEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Role name (unique)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Role display name
    /// </summary>
    [MaxLength(100)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Role description
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Whether this is a system role (cannot be deleted)
    /// </summary>
    public bool IsSystemRole { get; set; } = false;

    /// <summary>
    /// Whether role is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last updated timestamp
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual ICollection<RolePermissionEntity> Permissions { get; set; } = new List<RolePermissionEntity>();
}

/// <summary>
/// Entity for role-permission mappings
/// </summary>
public class RolePermissionEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Role ID
    /// </summary>
    public int RoleId { get; set; }

    /// <summary>
    /// Permission string
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Permission { get; set; } = null!;

    // Navigation
    public virtual RoleEntity Role { get; set; } = null!;
}
