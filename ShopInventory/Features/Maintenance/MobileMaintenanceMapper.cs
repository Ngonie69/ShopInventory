using ShopInventory.DTOs;
using ShopInventory.Features.AppVersion;

namespace ShopInventory.Features.Maintenance;

/// <summary>
/// Turns the stored lockout into what the settings screen and the apps are shown.
/// </summary>
internal static class MobileMaintenanceMapper
{
    public static MobileMaintenanceSettingsDto ToSettings(MobileMaintenanceState state, DateTime nowUtc) => new()
    {
        Enabled = state.Enabled,
        IsActive = state.IsActiveAt(nowUtc),
        Scope = state.Scope.ToString(),
        Message = state.Message ?? string.Empty,
        DefaultMessage = MobileMaintenanceState.DefaultMessage,
        AppIds = [.. state.AppIds],
        CoveredApps = [.. state.ResolveCoveredAppIds().Select(ToApp)],
        AvailableApps = [.. MobileVersionPolicyAppCatalog.SupportedPolicyKeys.Select(ToApp)],
        StartedAtUtc = state.StartedAtUtc,
        EndsAtUtc = state.EndsAtUtc,
        UpdatedBy = state.UpdatedBy ?? string.Empty
    };

    /// <summary>
    /// What one app is told when it asks.
    /// </summary>
    /// <remarks>
    /// Answered per app rather than as a single global figure, because a lockout narrowed to van
    /// sales must not make the POD app grey out its buttons. An app that does not name itself is
    /// told what a blanket lockout would do to it — the same answer the gate would give it.
    /// </remarks>
    public static MobileMaintenanceStatusDto ToStatus(
        MobileMaintenanceState state,
        string? policyKey,
        DateTime nowUtc)
    {
        var applies = state.IsActiveAt(nowUtc) && state.CoversApp(policyKey);

        return new MobileMaintenanceStatusDto
        {
            IsActive = applies,
            Scope = state.Scope.ToString(),
            Message = applies ? state.ResolveMessage() : string.Empty,
            ReadsAllowed = !applies || state.Scope == MobileMaintenanceScope.Transactions,
            EndsAtUtc = applies ? state.EndsAtUtc : null,
            CheckedAtUtc = nowUtc
        };
    }

    private static MobileMaintenanceAppDto ToApp(string policyKey) => new()
    {
        AppId = policyKey,
        DisplayName = MobileVersionPolicyAppCatalog.GetDisplayName(policyKey)
    };
}
