using System.Globalization;
using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.AppVersion;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Maintenance.Commands.SetMaintenance;

public sealed class SetMaintenanceHandler(
    IMaintenanceStore store,
    IAuditService auditService,
    TimeProvider timeProvider,
    ILogger<SetMaintenanceHandler> logger
) : IRequestHandler<SetMaintenanceCommand, ErrorOr<SetMaintenanceResponse>>
{
    public async Task<ErrorOr<SetMaintenanceResponse>> Handle(
        SetMaintenanceCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var previous = store.Current;

        var endsAtUtc = NormalizeEndsAt(request.EndsAtUtc);

        // A window that has already closed would be stored, shown as "maintenance on", and refuse
        // nothing. Better to say so than to let somebody walk away believing the phones are stopped.
        if (request.Enabled && endsAtUtc is { } endsAt && endsAt <= nowUtc)
        {
            return Errors.Maintenance.EndsInThePast;
        }

        var appIds = ResolveAppIds(request.AppIds);
        if (appIds.IsError)
        {
            return appIds.Errors;
        }

        var audiences = ResolveAudiences(request.Audiences);
        if (audiences.IsError)
        {
            return audiences.Errors;
        }

        var state = new MaintenanceState(
            Enabled: request.Enabled,
            Scope: ResolveScope(request.Scope),
            Audiences: audiences.Value,
            Message: string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
            AppIds: appIds.Value,
            // The clock starts when the lockout does. Re-saving a running lockout — to widen the
            // scope, or to correct the message — keeps the original start, because that is the time
            // somebody reading the audit trail wants.
            StartedAtUtc: request.Enabled
                ? previous.Enabled ? previous.StartedAtUtc ?? nowUtc : nowUtc
                : null,
            EndsAtUtc: request.Enabled ? endsAtUtc : null,
            UpdatedBy: command.UserName);

        try
        {
            await store.UpdateAsync(state, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set the maintenance lockout");
            return Errors.Maintenance.UpdateFailed(ex.Message);
        }

        await LogAuditAsync(state);

        return new SetMaintenanceResponse
        {
            Message = DescribeOutcome(state),
            Settings = MaintenanceMapper.ToSettings(state, nowUtc)
        };
    }

    /// <summary>
    /// Every end time is stored and compared in UTC.
    /// </summary>
    /// <remarks>
    /// A local time from the browser arrives as <see cref="DateTimeKind.Local"/> or
    /// <see cref="DateTimeKind.Unspecified"/>. Unspecified is read as UTC rather than as server
    /// local time, because the API and the operator are not reliably in the same time zone and the
    /// screen sends UTC.
    /// </remarks>
    private static DateTime? NormalizeEndsAt(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } utc => utc,
        { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        var unspecified => DateTime.SpecifyKind(unspecified.Value, DateTimeKind.Utc)
    };

    private static MaintenanceScope ResolveScope(string? scope) =>
        Enum.TryParse<MaintenanceScope>(scope?.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : MaintenanceScope.Transactions;

    /// <summary>
    /// The requested audiences, or the phones when none were named.
    /// </summary>
    /// <remarks>
    /// An empty list is read as the mobile apps rather than as "nobody", for two reasons. It is
    /// what the switch covered before audiences existed, so an older caller — the settings screen
    /// mid-deploy, a script, a hand-written PUT — keeps meaning what it used to mean. And a lockout
    /// that is on and covers nobody is the one state worth never storing: the screen would say
    /// maintenance is running while every client kept trading.
    /// </remarks>
    private static ErrorOr<IReadOnlyList<MaintenanceAudience>> ResolveAudiences(List<string>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return ErrorOrFactory.From(MaintenanceAudiences.Default);
        }

        var resolved = new List<MaintenanceAudience>(requested.Count);
        foreach (var entry in requested.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!MaintenanceAudiences.TryParse(entry, out var audience))
            {
                return Errors.Maintenance.UnsupportedAudience(entry);
            }

            if (!resolved.Contains(audience))
            {
                resolved.Add(audience);
            }
        }

        return resolved.Count == 0
            ? ErrorOrFactory.From(MaintenanceAudiences.Default)
            : ErrorOrFactory.From<IReadOnlyList<MaintenanceAudience>>(resolved);
    }

    /// <summary>
    /// The requested apps as catalogue keys. Empty stays empty, and empty means every app.
    /// </summary>
    private static ErrorOr<IReadOnlyList<string>> ResolveAppIds(List<string>? requested)
    {
        IReadOnlyList<string> everyApp = [];

        if (requested is null || requested.Count == 0)
        {
            return ErrorOrFactory.From(everyApp);
        }

        var resolved = new List<string>(requested.Count);
        foreach (var appId in requested.Where(entry => !string.IsNullOrWhiteSpace(entry)))
        {
            if (!MobileVersionPolicyAppCatalog.TryResolvePolicyKey(appId, out var policyKey))
            {
                return Errors.Maintenance.UnsupportedApp(appId);
            }

            if (!resolved.Contains(policyKey, StringComparer.OrdinalIgnoreCase))
            {
                resolved.Add(policyKey);
            }
        }

        // Naming every app is the same lockout as naming none, and an empty list is the form that
        // keeps covering a new app added to the catalogue later.
        return resolved.Count == MobileVersionPolicyAppCatalog.SupportedPolicyKeys.Length
            ? ErrorOrFactory.From(everyApp)
            : ErrorOrFactory.From<IReadOnlyList<string>>(resolved);
    }

    private async Task LogAuditAsync(MaintenanceState state)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.SetMaintenance,
                "Maintenance",
                state.Enabled ? "on" : "off",
                DescribeForAudit(state),
                true);
        }
        catch (Exception ex)
        {
            // The switch has already been thrown and is already in force. Losing the audit row is
            // worth a warning, not a failed response that would have an operator throw it again.
            logger.LogWarning(ex, "Could not write the audit entry for the maintenance switch");
        }
    }

    private static string DescribeForAudit(MaintenanceState state)
    {
        if (!state.Enabled)
        {
            return $"Maintenance lockout lifted by {state.UpdatedBy}";
        }

        var until = state.EndsAtUtc is { } endsAt
            ? $" until {endsAt.ToString("u", CultureInfo.InvariantCulture)}"
            : " until turned off";

        return $"Maintenance lockout ({state.Scope}) turned on for {DescribeAudiences(state)}{until} by {state.UpdatedBy}";
    }

    private static string DescribeOutcome(MaintenanceState state)
    {
        if (!state.Enabled)
        {
            return "Maintenance mode is off. Everything can transact again.";
        }

        var withheld = state.Scope == MaintenanceScope.All
            ? "cannot reach the system at all"
            : "cannot transact, but can still read";

        var until = state.EndsAtUtc is { } endsAt
            ? $" Lifts automatically at {endsAt.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC."
            : " It stays on until you turn it off.";

        var admins = state.CoversAudience(MaintenanceAudience.WebPortal)
            ? " Admins can still use the web portal."
            : string.Empty;

        return $"Maintenance mode is on. {DescribeAudiences(state)} {withheld}.{until}{admins}";
    }

    /// <summary>
    /// The audiences in the words an operator uses, with the mobile apps spelled out when the
    /// lockout has been narrowed to some of them.
    /// </summary>
    private static string DescribeAudiences(MaintenanceState state)
    {
        var parts = state.ResolveAudiences().Select(audience =>
            audience == MaintenanceAudience.MobileApps && state.AppIds.Count > 0
                ? string.Join(", ", state.AppIds.Select(MobileVersionPolicyAppCatalog.GetDisplayName))
                : MaintenanceAudiences.GetDisplayName(audience));

        return string.Join(", ", parts);
    }
}
