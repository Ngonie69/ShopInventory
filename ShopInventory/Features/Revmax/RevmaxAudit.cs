using ShopInventory.Services;

namespace ShopInventory.Features.Revmax;

internal static class RevmaxAudit
{
    internal const string EntityType = "Revmax";
    internal const string TransactionEntityType = "RevmaxTransaction";

    internal static bool IsSuccessCode(string? code)
        => string.Equals(code, "1", StringComparison.Ordinal);

    internal static async Task TryLogAsync(
        IAuditService auditService,
        string action,
        string entityType,
        string? entityId,
        string? details,
        bool isSuccess,
        string? errorMessage = null)
    {
        try
        {
            await auditService.LogAsync(action, entityType, entityId, details, isSuccess, errorMessage);
        }
        catch
        {
        }
    }
}