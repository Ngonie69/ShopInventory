using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Service interface for Sales Order operations
/// </summary>
public interface ISalesOrderService
{
    Task<SalesOrderDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<SalesOrderDto?> GetByOrderNumberAsync(string orderNumber, CancellationToken cancellationToken = default);
    Task<SalesOrderListResponseDto> GetAllAsync(int page, int pageSize, SalesOrderStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);
    Task<SalesOrderDto> CreateAsync(CreateSalesOrderRequest request, Guid userId, CancellationToken cancellationToken = default);
    Task<SalesOrderDto> UpdateAsync(int id, CreateSalesOrderRequest request, CancellationToken cancellationToken = default);
    Task<SalesOrderDto> UpdateStatusAsync(int id, SalesOrderStatus status, Guid userId, string? comments = null, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task<SalesOrderDto> ApproveAsync(int id, Guid userId, CancellationToken cancellationToken = default);
    Task<InvoiceDto?> ConvertToInvoiceAsync(int id, Guid userId, CancellationToken cancellationToken = default);
    Task<string> GenerateOrderNumberAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Service interface for Credit Note operations
/// </summary>
public interface ICreditNoteService
{
    Task<CreditNoteDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<CreditNoteDto?> GetByCreditNoteNumberAsync(string creditNoteNumber, CancellationToken cancellationToken = default);
    Task<List<CreditNoteDto>> GetByInvoiceIdAsync(int invoiceId, CancellationToken cancellationToken = default);
    Task<CreditNoteListResponseDto> GetAllAsync(int page, int pageSize, CreditNoteStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default);
    Task<CreditNoteDto> CreateAsync(CreateCreditNoteRequest request, Guid userId, CancellationToken cancellationToken = default);
    Task<CreditNoteDto> CreateFromInvoiceAsync(int invoiceId, List<CreateCreditNoteLineRequest> lines, string reason,
        Guid userId, CancellationToken cancellationToken = default);
    Task<CreditNoteDto> UpdateStatusAsync(int id, CreditNoteStatus status, Guid userId, CancellationToken cancellationToken = default);
    Task<CreditNoteDto> ApproveAsync(int id, Guid userId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task<string> GenerateCreditNoteNumberAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Service interface for Exchange Rate operations
/// </summary>
public interface IExchangeRateService
{
    Task<ExchangeRateDto?> GetCurrentRateAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken = default);
    Task<List<ExchangeRateDto>> GetAllActiveRatesAsync(CancellationToken cancellationToken = default);
    Task<ExchangeRateHistoryDto> GetRateHistoryAsync(string fromCurrency, string toCurrency, int days = 30, CancellationToken cancellationToken = default);
    Task<ExchangeRateDto> UpsertRateAsync(UpsertExchangeRateRequest request, Guid userId, CancellationToken cancellationToken = default);
    Task<decimal> ConvertAsync(decimal amount, string fromCurrency, string toCurrency, CancellationToken cancellationToken = default);
    Task FetchExternalRatesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Service interface for System Configuration operations
/// </summary>
public interface ISystemConfigService
{
    Task<List<SystemConfigDto>> GetAllAsync(string? category = null, CancellationToken cancellationToken = default);
    Task<SystemConfigDto?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);
    Task<T?> GetValueAsync<T>(string key, CancellationToken cancellationToken = default);
    Task<SystemConfigDto> UpsertAsync(string key, string? value, string? category = null, string? description = null,
        bool isSensitive = false, Guid? userId = null, CancellationToken cancellationToken = default);
    Task<SAPConnectionSettingsDto> GetSAPSettingsAsync(CancellationToken cancellationToken = default);
    Task<bool> UpdateSAPSettingsAsync(UpdateSAPSettingsRequest request, Guid userId, CancellationToken cancellationToken = default);
    Task<bool> TestSAPConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Service interface for Backup operations
/// </summary>
public interface IBackupService
{
    Task<BackupListResponseDto> GetAllBackupsAsync(CancellationToken cancellationToken = default);
    Task<BackupDto?> GetBackupByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<BackupDto> CreateBackupAsync(CreateBackupRequest request, Guid userId, CancellationToken cancellationToken = default);
    Task<bool> RestoreBackupAsync(int backupId, Guid userId, CancellationToken cancellationToken = default);
    Task<bool> DeleteBackupAsync(int id, CancellationToken cancellationToken = default);
    Task<Stream?> DownloadBackupAsync(int id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Service interface for API Rate Limiting
/// </summary>
public interface IRateLimitService
{
    Task<RateLimitDashboardDto> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<ApiRateLimitDto?> GetClientLimitAsync(string clientId, CancellationToken cancellationToken = default);
    Task<bool> CheckRateLimitAsync(string clientId, string clientType, string? endpoint = null, CancellationToken cancellationToken = default);
    Task IncrementRequestCountAsync(string clientId, string clientType, string? endpoint = null, CancellationToken cancellationToken = default);
    Task<bool> UnblockClientAsync(string clientId, CancellationToken cancellationToken = default);
    Task UpdateSettingsAsync(UpdateRateLimitSettingsRequest settings, CancellationToken cancellationToken = default);
    Task CleanupOldRecordsAsync(CancellationToken cancellationToken = default);

    // Additional methods used by controller
    Task<RateLimitListResponseDto> GetAllAsync(int page, int pageSize, bool? blockedOnly = null, CancellationToken cancellationToken = default);
    Task<ApiRateLimitDto?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken = default);
    Task<RateLimitStatusDto> GetRateLimitStatusAsync(string clientId, CancellationToken cancellationToken = default);
    Task<bool> IsRequestAllowedAsync(string clientId, CancellationToken cancellationToken = default);
    Task BlockClientAsync(string clientId, int durationMinutes, string? reason = null, CancellationToken cancellationToken = default);
    Task<bool> ResetClientAsync(string clientId, CancellationToken cancellationToken = default);
    Task<List<ApiRateLimitDto>> GetBlockedClientsAsync(CancellationToken cancellationToken = default);
    Task<RateLimitStatsDto> GetStatsAsync(CancellationToken cancellationToken = default);
    RateLimitConfigDto GetConfiguration();
    Task UpdateConfigurationAsync(RateLimitConfigDto config, CancellationToken cancellationToken = default);
    Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Service interface for Role and Permission management
/// </summary>
public interface IRolePermissionService
{
    // Role operations
    Task<List<RoleDto>> GetAllRolesAsync(CancellationToken cancellationToken = default);
    Task<RoleDto?> GetRoleByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<RoleDto?> GetRoleByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<RoleDto> CreateRoleAsync(UpsertRoleRequest request, CancellationToken cancellationToken = default);
    Task<RoleDto> UpdateRoleAsync(int id, UpsertRoleRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteRoleAsync(int id, CancellationToken cancellationToken = default);

    // Permission operations
    Task<List<PermissionGroupDto>> GetAllPermissionsGroupedAsync(CancellationToken cancellationToken = default);
    Task<List<UserPermissionDto>> GetUserPermissionsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<bool> AssignPermissionsToUserAsync(AssignUserPermissionsRequest request, Guid assignedByUserId, CancellationToken cancellationToken = default);
    Task<bool> RemoveUserPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default);
    Task<bool> UserHasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default);
    Task<List<string>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Service interface for Invoice Email operations
/// </summary>
public interface IInvoiceEmailService
{
    Task<SendEmailResponseDto> SendInvoiceEmailAsync(SendInvoiceEmailRequest request, CancellationToken cancellationToken = default);
    Task<byte[]> GenerateInvoicePdfAsync(int invoiceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Service interface for Import/Export operations
/// </summary>
public interface IImportExportService
{
    // Export
    Task<ExportResponseDto> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default);
    Task<List<string>> GetExportableEntitiesAsync(CancellationToken cancellationToken = default);

    // Import
    Task<ImportResultDto> ImportAsync(string entityType, Stream fileStream, string fileName, CancellationToken cancellationToken = default);
    Task<ImportTemplateDto> GetImportTemplateAsync(string entityType, CancellationToken cancellationToken = default);
    Task<byte[]> DownloadImportTemplateAsync(string entityType, CancellationToken cancellationToken = default);
    Task<List<string>> GetImportableEntitiesAsync(CancellationToken cancellationToken = default);
}
