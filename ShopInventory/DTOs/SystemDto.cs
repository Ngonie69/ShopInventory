using System.ComponentModel.DataAnnotations;

namespace ShopInventory.DTOs;

#region Exchange Rate DTOs

/// <summary>
/// DTO for Exchange Rate
/// </summary>
public class ExchangeRateDto
{
    public int Id { get; set; }
    public string FromCurrency { get; set; } = null!;
    public string ToCurrency { get; set; } = null!;
    public decimal Rate { get; set; }
    public decimal InverseRate { get; set; }
    public DateTime EffectiveDate { get; set; }
    public string? Source { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Request to create/update exchange rate
/// </summary>
public class UpsertExchangeRateRequest
{
    [Required]
    [MaxLength(10)]
    public string FromCurrency { get; set; } = null!;

    [Required]
    [MaxLength(10)]
    public string ToCurrency { get; set; } = null!;

    [Range(0.000001, double.MaxValue, ErrorMessage = "Rate must be positive")]
    public decimal Rate { get; set; }

    public DateTime? EffectiveDate { get; set; }

    [MaxLength(50)]
    public string? Source { get; set; }
}

/// <summary>
/// Exchange rate history response
/// </summary>
public class ExchangeRateHistoryDto
{
    public string FromCurrency { get; set; } = null!;
    public string ToCurrency { get; set; } = null!;
    public List<ExchangeRateDto> History { get; set; } = new();
}

/// <summary>
/// Request to create exchange rate (alias for UpsertExchangeRateRequest)
/// </summary>
public class CreateExchangeRateRequest : UpsertExchangeRateRequest
{
}

#endregion

#region System Configuration DTOs

/// <summary>
/// DTO for System Configuration
/// </summary>
public class SystemConfigDto
{
    public int Id { get; set; }
    public string Key { get; set; } = null!;
    public string? Value { get; set; }
    public string ValueType { get; set; } = "string";
    public string? Category { get; set; }
    public string? Description { get; set; }
    public bool IsEditable { get; set; }
    public bool IsSensitive { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Request to update system configuration
/// </summary>
public class UpdateSystemConfigRequest
{
    [Required]
    public string Key { get; set; } = null!;

    public string? Value { get; set; }
}

/// <summary>
/// SAP Connection settings DTO
/// </summary>
public class SAPConnectionSettingsDto
{
    public string? ServiceLayerUrl { get; set; }
    public string? CompanyDB { get; set; }
    public string? UserName { get; set; }
    public bool IsConfigured { get; set; }
    public bool IsConnected { get; set; }
    public DateTime? LastConnectionTime { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Request to update SAP settings
/// </summary>
public class UpdateSAPSettingsRequest
{
    [Required]
    public string ServiceLayerUrl { get; set; } = null!;

    [Required]
    public string CompanyDB { get; set; } = null!;

    [Required]
    public string UserName { get; set; } = null!;

    [Required]
    public string Password { get; set; } = null!;

    public bool TestConnection { get; set; } = true;
}

/// <summary>
/// SAP Settings DTO (read-only, masks sensitive data)
/// </summary>
public class SAPSettingsDto
{
    public string? ServiceLayerUrl { get; set; }
    public string? CompanyDB { get; set; }
    public string? UserName { get; set; }
    public bool IsConfigured { get; set; }
    public bool IsConnected { get; set; }
    public DateTime? LastConnectionTime { get; set; }
}

/// <summary>
/// Email settings DTO
/// </summary>
public class EmailSettingsDto
{
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? SmtpUsername { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
    public bool IsConfigured { get; set; }
}

/// <summary>
/// Connection test result DTO
/// </summary>
public class ConnectionTestResultDto
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public double ResponseTimeMs { get; set; }
    public DateTime TestedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Details { get; set; } = new();
}

#endregion

#region Backup DTOs

/// <summary>
/// DTO for Backup
/// </summary>
public class BackupDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = null!;
    public string? FilePath { get; set; }
    public long SizeBytes { get; set; }
    public string SizeFormatted => FormatSize(SizeBytes);
    public string BackupType { get; set; } = "Full";
    public string Status { get; set; } = "InProgress";
    public string? ErrorMessage { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration => CompletedAt?.Subtract(StartedAt);
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public string? Description { get; set; }
    public bool IsOffsite { get; set; }
    public string? CloudUrl { get; set; }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Request to create a backup
/// </summary>
public class CreateBackupRequest
{
    public string BackupType { get; set; } = "Full";
    public string? Description { get; set; }
    public bool UploadToCloud { get; set; } = false;
}

/// <summary>
/// Request to restore from backup
/// </summary>
public class RestoreBackupRequest
{
    [Required]
    public int BackupId { get; set; }

    public bool ConfirmRestore { get; set; } = false;
}

/// <summary>
/// Backup list response
/// </summary>
public class BackupListResponseDto
{
    public int TotalCount { get; set; }
    public List<BackupDto> Backups { get; set; } = new();
}

/// <summary>
/// Backup statistics DTO
/// </summary>
public class BackupStatsDto
{
    public int TotalBackups { get; set; }
    public int SuccessfulBackups { get; set; }
    public int FailedBackups { get; set; }
    public long TotalSizeBytes { get; set; }
    public string TotalSizeFormatted { get; set; } = "0 B";
    public DateTime? LastBackupAt { get; set; }
    public DateTime? NextScheduledBackup { get; set; }
    public int BackupsLast24Hours { get; set; }
    public int BackupsLast7Days { get; set; }
}

#endregion

#region Rate Limit DTOs

/// <summary>
/// DTO for API Rate Limit
/// </summary>
public class ApiRateLimitDto
{
    public int Id { get; set; }
    public string ClientId { get; set; } = null!;
    public string ClientType { get; set; } = "IP";
    public string? Endpoint { get; set; }
    public int RequestCount { get; set; }
    public DateTime WindowStart { get; set; }
    public int WindowDurationSeconds { get; set; }
    public int MaxRequests { get; set; }
    public bool IsBlocked { get; set; }
    public DateTime? BlockExpiresAt { get; set; }
    public int TotalBlockedCount { get; set; }
    public DateTime LastRequestAt { get; set; }
    public double UsagePercent => MaxRequests > 0 ? (double)RequestCount / MaxRequests * 100 : 0;
}

/// <summary>
/// Rate limit dashboard summary
/// </summary>
public class RateLimitDashboardDto
{
    public int TotalClients { get; set; }
    public int BlockedClients { get; set; }
    public int TotalRequests24h { get; set; }
    public int TotalBlocks24h { get; set; }
    public List<ApiRateLimitDto> TopClients { get; set; } = new();
    public List<ApiRateLimitDto> BlockedClientsList { get; set; } = new();
}

/// <summary>
/// Request to update rate limit settings
/// </summary>
public class UpdateRateLimitSettingsRequest
{
    public int DefaultMaxRequests { get; set; } = 100;
    public int DefaultWindowSeconds { get; set; } = 60;
    public int BlockDurationMinutes { get; set; } = 15;
}

/// <summary>
/// Request to unblock a client
/// </summary>
public class UnblockClientRequest
{
    [Required]
    public string ClientId { get; set; } = null!;
}

/// <summary>
/// Rate limit list response DTO
/// </summary>
public class RateLimitListResponseDto
{
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<ApiRateLimitDto> Items { get; set; } = new();
}

/// <summary>
/// Rate limit status DTO for current client
/// </summary>
public class RateLimitStatusDto
{
    public string ClientId { get; set; } = null!;
    public int RequestsInWindow { get; set; }
    public int MaxRequests { get; set; }
    public int WindowSizeSeconds { get; set; }
    public DateTime WindowResetAt { get; set; }
    public bool IsBlocked { get; set; }
    public DateTime? BlockedUntil { get; set; }
    public int RemainingRequests => Math.Max(0, MaxRequests - RequestsInWindow);
}

/// <summary>
/// Rate limit statistics DTO
/// </summary>
public class RateLimitStatsDto
{
    public int TotalClients { get; set; }
    public int ActiveClients { get; set; }
    public int BlockedClients { get; set; }
    public int TotalRequestsToday { get; set; }
    public int TotalBlocksToday { get; set; }
    public double AverageRequestsPerClient { get; set; }
}

/// <summary>
/// Rate limit configuration DTO
/// </summary>
public class RateLimitConfigDto
{
    public int MaxRequests { get; set; } = 100;
    public int WindowSizeSeconds { get; set; } = 60;
    public int BlockDurationMinutes { get; set; } = 15;
    public bool IsEnabled { get; set; } = true;
    public List<string> WhitelistedIPs { get; set; } = new();
    public List<string> WhitelistedApiKeys { get; set; } = new();
}

#endregion

#region Role/Permission DTOs

/// <summary>
/// DTO for Role
/// </summary>
public class RoleDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool IsSystemRole { get; set; }
    public bool IsActive { get; set; }
    public int UserCount { get; set; }
    public List<string> Permissions { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Request to create/update role
/// </summary>
public class UpsertRoleRequest
{
    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = null!;

    [MaxLength(100)]
    public string? DisplayName { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public List<string> Permissions { get; set; } = new();
}

/// <summary>
/// Request to create a new role (alias for UpsertRoleRequest)
/// </summary>
public class CreateRoleRequest : UpsertRoleRequest
{
}

/// <summary>
/// Request to update an existing role (alias for UpsertRoleRequest)
/// </summary>
public class UpdateRoleRequest : UpsertRoleRequest
{
}

/// <summary>
/// DTO for User Permission
/// </summary>
public class UserPermissionDto
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = null!;
    public string Permission { get; set; } = null!;
    public bool IsGranted { get; set; }
    public DateTime AssignedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? AssignedByUserName { get; set; }
}

/// <summary>
/// Request to assign permissions to user
/// </summary>
public class AssignUserPermissionsRequest
{
    [Required]
    public Guid UserId { get; set; }

    public List<string> PermissionsToGrant { get; set; } = new();
    public List<string> PermissionsToDeny { get; set; } = new();
}

/// <summary>
/// Permission group for UI display
/// </summary>
public class PermissionGroupDto
{
    public string Category { get; set; } = null!;
    public List<PermissionItemDto> Permissions { get; set; } = new();
}

/// <summary>
/// Permission item for UI display
/// </summary>
public class PermissionItemDto
{
    public string Key { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public bool IsGranted { get; set; }
}

#endregion

#region Email Invoice DTOs

/// <summary>
/// Request to send invoice via email
/// </summary>
public class SendInvoiceEmailRequest
{
    [Required]
    public int InvoiceId { get; set; }

    [Required]
    [EmailAddress]
    public string RecipientEmail { get; set; } = null!;

    public string? RecipientName { get; set; }
    public string? Subject { get; set; }
    public string? Message { get; set; }
    public bool AttachPdf { get; set; } = true;
    public List<string>? CcEmails { get; set; }
}

/// <summary>
/// Response for email send operation
/// </summary>
public class SendEmailResponseDto
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? MessageId { get; set; }
    public DateTime? SentAt { get; set; }
}

#endregion

#region Import/Export DTOs

/// <summary>
/// Import result DTO
/// </summary>
public class ImportResultDto
{
    public bool Success { get; set; }
    public int TotalRows { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public int SkippedCount { get; set; }
    public List<ImportErrorDto> Errors { get; set; } = new();
    public string? Message { get; set; }
}

/// <summary>
/// Import error details
/// </summary>
public class ImportErrorDto
{
    public int RowNumber { get; set; }
    public string? Field { get; set; }
    public string? Value { get; set; }
    public string Error { get; set; } = null!;
}

/// <summary>
/// Export request
/// </summary>
public class ExportRequest
{
    [Required]
    public string EntityType { get; set; } = null!; // Products, Invoices, Customers, etc.

    public string Format { get; set; } = "csv"; // csv, xlsx

    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public List<string>? Columns { get; set; }
    public Dictionary<string, string>? Filters { get; set; }
}

/// <summary>
/// Export response
/// </summary>
public class ExportResponseDto
{
    public bool Success { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public byte[]? Data { get; set; }
    public int RowCount { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Import template info
/// </summary>
public class ImportTemplateDto
{
    public string EntityType { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public List<ImportColumnDto> Columns { get; set; } = new();
}

/// <summary>
/// Import column definition
/// </summary>
public class ImportColumnDto
{
    public string Name { get; set; } = null!;
    public string DataType { get; set; } = "string";
    public bool IsRequired { get; set; }
    public string? Description { get; set; }
    public string? SampleValue { get; set; }
}

#endregion
