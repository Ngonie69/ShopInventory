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

        var state = new MaintenanceState(
            Enabled: request.Enabled,
            Scope: ResolveScope(request.Scope),
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
            logger.LogError(ex, "Failed to set the mobile maintenance lockout");
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
            logger.LogWarning(ex, "Could not write the audit entry for the mobile maintenance switch");
        }
    }

    private static string DescribeForAudit(MaintenanceState state)
    {
        if (!state.Enabled)
        {
            return $"Mobile maintenance lockout lifted by {state.UpdatedBy}";
        }

        var apps = state.AppIds.Count == 0
            ? "all mobile apps"
            : string.Join(", ", state.AppIds.Select(MobileVersionPolicyAppCatalog.GetDisplayName));

        var until = state.EndsAtUtc is { } endsAt
            ? $" until {endsAt.ToString("u", CultureInfo.InvariantCulture)}"
            : " until turned off";

        return $"Mobile maintenance lockout ({state.Scope}) turned on for {apps}{until} by {state.UpdatedBy}";
    }

    private static string DescribeOutcome(MaintenanceState state)
    {
        if (!state.Enabled)
        {
            return "Maintenance mode is off. The mobile apps can transact again.";
        }

        var apps = state.AppIds.Count == 0
            ? "All mobile apps"
            : string.Join(", ", state.AppIds.Select(MobileVersionPolicyAppCatalog.GetDisplayName));

        var withheld = state.Scope == MaintenanceScope.All
            ? "cannot reach the system at all"
            : "cannot transact, but can still read";

        var until = state.EndsAtUtc is { } endsAt
            ? $" Lifts automatically at {endsAt.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC."
            : " It stays on until you turn it off.";

        return $"Maintenance mode is on. {apps} {withheld}.{until}";
    }
}
