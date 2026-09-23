using ShopInventory.Features.AppVersion;

namespace ShopInventory.Features.Maintenance;

/// <summary>
/// The maintenance lockout as an operator set it, and the reading of it that the gate applies.
/// </summary>
/// <param name="Enabled">Whether an operator has turned the lockout on.</param>
/// <param name="Scope">How much is withheld while it is on.</param>
/// <param name="Audiences">
/// Who the lockout applies to. Never empty: a lockout that is on and covers nobody would read on
/// the screen as "maintenance is running" while everything kept trading, so the store and the
/// handler both fall back to <see cref="MaintenanceAudiences.Default"/> rather than store one.
/// </param>
/// <param name="Message">What the phones are told. Blank falls back to <see cref="DefaultMessage"/>.</param>
/// <param name="AppIds">
/// The catalogue keys the lockout covers within <see cref="MaintenanceAudience.MobileApps"/>. Empty
/// means every app, which is what "stop the phones" normally means; naming apps is for the narrower
/// case of maintenance that only touches one. It has no bearing on the other audiences.
/// </param>
/// <param name="StartedAtUtc">When it was switched on, for the audit trail and the status endpoint.</param>
/// <param name="EndsAtUtc">
/// When it lifts on its own, if an operator set an end. See <see cref="IsActiveAt"/>.
/// </param>
/// <param name="UpdatedBy">Who last touched the switch.</param>
public sealed record MaintenanceState(
    bool Enabled,
    MaintenanceScope Scope,
    IReadOnlyList<MaintenanceAudience> Audiences,
    string? Message,
    IReadOnlyList<string> AppIds,
    DateTime? StartedAtUtc,
    DateTime? EndsAtUtc,
    string? UpdatedBy)
{
    /// <summary>What an app shows when the operator did not write anything better.</summary>
    public const string DefaultMessage =
        "The system is down for maintenance. You can't send anything through right now — "
        + "please try again shortly.";

    /// <summary>A deployment that has never had the switch touched.</summary>
    public static readonly MaintenanceState Off = new(
        Enabled: false,
        Scope: MaintenanceScope.Transactions,
        Audiences: MaintenanceAudiences.Default,
        Message: null,
        AppIds: [],
        StartedAtUtc: null,
        EndsAtUtc: null,
        UpdatedBy: null);

    /// <summary>
    /// Whether the lockout is actually in force at <paramref name="nowUtc"/>.
    /// </summary>
    /// <remarks>
    /// An end time that has passed lifts the lockout without anybody having to flip the switch
    /// back. This is the whole point of storing an end time: the failure this feature invites is
    /// maintenance finishing at 02:00 and the phones still being locked out at 08:00 because the
    /// person who turned it on went to bed. An operator who wants it on until they say so leaves
    /// the end time blank.
    /// </remarks>
    public bool IsActiveAt(DateTime nowUtc) =>
        Enabled && (EndsAtUtc is null || nowUtc < EndsAtUtc.Value);

    /// <summary>
    /// Whether this lockout is aimed at the given audience.
    /// </summary>
    public bool CoversAudience(MaintenanceAudience audience) => ResolveAudiences().Contains(audience);

    /// <summary>
    /// Whether this lockout covers the given app.
    /// </summary>
    /// <param name="policyKey">
    /// The catalogue key from the caller's <c>X-App-Id</c>, or null when the build is too old to
    /// send one.
    /// </param>
    /// <remarks>
    /// An unidentified phone is covered by a lockout that names no apps, and is not covered by one
    /// that does. Both halves are deliberate. A blanket lockout must not be escapable by an app
    /// that declines to identify itself — that is the case the switch exists for. But a lockout
    /// aimed at, say, van sales alone must not take down the POD app because a handset on an old
    /// build could not say which one it was.
    /// </remarks>
    public bool CoversApp(string? policyKey) =>
        AppIds.Count == 0
        || (policyKey is not null && AppIds.Contains(policyKey, StringComparer.OrdinalIgnoreCase));

    /// <summary>The message to send, never blank.</summary>
    public string ResolveMessage() =>
        string.IsNullOrWhiteSpace(Message) ? DefaultMessage : Message.Trim();

    /// <summary>The app keys this covers, spelled out, for a screen that has to show them.</summary>
    public IReadOnlyList<string> ResolveCoveredAppIds() =>
        AppIds.Count == 0 ? MobileVersionPolicyAppCatalog.SupportedPolicyKeys : AppIds;

    /// <summary>
    /// The audiences this covers, never empty.
    /// </summary>
    /// <remarks>
    /// Guards the one state that would be quietly useless: enabled, with nobody named. Everything
    /// that writes a state resolves the audiences first, so this only ever fires on a row somebody
    /// edited by hand — and when it does, falling back to the phones matches what the switch did
    /// before audiences existed.
    /// </remarks>
    public IReadOnlyList<MaintenanceAudience> ResolveAudiences() =>
        Audiences.Count == 0 ? MaintenanceAudiences.Default : Audiences;
}
