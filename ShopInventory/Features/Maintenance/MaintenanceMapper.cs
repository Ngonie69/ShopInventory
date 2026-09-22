using ShopInventory.DTOs;
using ShopInventory.Features.AppVersion;

namespace ShopInventory.Features.Maintenance;

/// <summary>
/// Turns the stored lockout into what the settings screen and the apps are shown.
/// </summary>
internal static class MaintenanceMapper
{
    public static MaintenanceSettingsDto ToSettings(MaintenanceState state, DateTime nowUtc) => new()
    {
        Enabled = state.Enabled,
        IsActive = state.IsActiveAt(nowUtc),
        Scope = state.Scope.ToString(),
        Audiences = [.. state.ResolveAudiences().Select(audience => audience.ToString())],
        AvailableAudiences = [.. MaintenanceAudiences.All.Select(ToAudience)],
        Message = state.Message ?? string.Empty,
        DefaultMessage = MaintenanceState.DefaultMessage,
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
    /// Answered per caller rather than as a single global figure, because a lockout aimed at the
    /// phones must not make the web portal put up a banner, and one narrowed to van sales must not
    /// make the POD app grey out its buttons. An app that does not name itself is told what a
    /// blanket lockout would do to it — the same answer the gate would give it.
    ///
    /// It does not account for the Admin exemption: an admin asking gets "the portal is frozen",
    /// which is true and is what the banner should say. Telling them otherwise would hide from the
    /// person running the maintenance that everybody else has stopped.
    /// </remarks>
    public static MaintenanceStatusDto ToStatus(
        MaintenanceState state,
        MaintenanceCaller caller,
        DateTime nowUtc)
    {
        var applies = state.IsActiveAt(nowUtc)
            && state.CoversAudience(caller.Audience)
            && (caller.Audience != MaintenanceAudience.MobileApps || state.CoversApp(caller.PolicyKey));

        return new MaintenanceStatusDto
        {
            IsActive = applies,
            Audience = caller.Audience.ToString(),
            Scope = state.Scope.ToString(),
            Message = applies ? state.ResolveMessage() : string.Empty,
            ReadsAllowed = !applies || state.Scope == MaintenanceScope.Transactions,
            EndsAtUtc = applies ? state.EndsAtUtc : null,
            CheckedAtUtc = nowUtc
        };
    }

    private static MaintenanceAudienceDto ToAudience(MaintenanceAudience audience) => new()
    {
        Audience = audience.ToString(),
        DisplayName = MaintenanceAudiences.GetDisplayName(audience),
        Description = MaintenanceAudiences.GetDescription(audience)
    };

    private static MaintenanceAppDto ToApp(string policyKey) => new()
    {
        AppId = policyKey,
        DisplayName = MobileVersionPolicyAppCatalog.GetDisplayName(policyKey)
    };
}
