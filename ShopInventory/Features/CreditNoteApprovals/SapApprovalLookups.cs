using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals;

public sealed class SapApprovalLookups(
    ISAPServiceLayerClient sap,
    IMemoryCache cache,
    IOptions<SAPSettings> settings) : ISapApprovalLookups
{
    private static readonly TimeSpan HitLifetime = TimeSpan.FromMinutes(10);

    // A miss is held briefly too, so a list of fifty rows naming a deleted user asks SAP once, not fifty times.
    private static readonly TimeSpan MissLifetime = TimeSpan.FromMinutes(1);

    public string ServiceApproverUserCode => settings.Value.ResolveApprovalApproverUsername();

    public Task<SAPUser?> GetServiceApproverAsync(CancellationToken cancellationToken)
    {
        var userCode = ServiceApproverUserCode;
        return GetOrReadAsync($"SapApproval_UserCode_{userCode}", () => sap.GetSapUserByCodeAsync(userCode, cancellationToken));
    }

    public Task<SAPUser?> GetUserAsync(int internalKey, CancellationToken cancellationToken)
        => GetOrReadAsync($"SapApproval_User_{internalKey}", () => sap.GetSapUserAsync(internalKey, cancellationToken));

    public Task<SAPApprovalTemplate?> GetTemplateAsync(int code, CancellationToken cancellationToken)
        => GetOrReadAsync($"SapApproval_Template_{code}", () => sap.GetApprovalTemplateAsync(code, cancellationToken));

    public Task<SAPApprovalStage?> GetStageAsync(int code, CancellationToken cancellationToken)
        => GetOrReadAsync($"SapApproval_Stage_{code}", () => sap.GetApprovalStageAsync(code, cancellationToken));

    private async Task<T?> GetOrReadAsync<T>(string key, Func<Task<T?>> read) where T : class
    {
        if (cache.TryGetValue(key, out object? cached))
        {
            return cached as T;
        }

        var value = await read();
        cache.Set(key, value, value is null ? MissLifetime : HitLifetime);
        return value;
    }
}
