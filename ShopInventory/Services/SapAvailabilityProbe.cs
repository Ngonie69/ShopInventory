using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Middleware;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// One probe of SAP, and the outage bookkeeping that follows from it.
/// </summary>
/// <remarks>
/// <para>
/// An outage opens after <see cref="SapAvailabilitySettings.FailuresToDeclare"/> failed probes in a
/// row and is dated from the first of them; it ends after
/// <see cref="SapAvailabilitySettings.SuccessesToEnd"/> good probes in a row and is dated to the first
/// of those. The run so far travels in <see cref="SapProbeRun"/>, which the Quartz job persists between
/// firings — the job may fire on a different node each time.
/// </para>
/// <para>
/// An admin turning SAP off opens an outage at once, without probing: nothing may be sent. Turning it
/// back on does not close it. Probing resumes, and the outage ends when SAP answers, which is the only
/// thing that proves it is usable.
/// </para>
/// <para>
/// A probe refused by this node's open circuit counts as a failure. The circuit only opens on failed
/// requests, and it closes on the first success, so it cannot keep a recovered SAP looking down.
/// </para>
/// </remarks>
public sealed class SapAvailabilityProbe(
    ApplicationDbContext db,
    ISAPServiceLayerClient sap,
    SapCircuitBreakerState circuit,
    SapAvailability availability,
    IOptions<SapAvailabilitySettings> settings,
    TimeProvider timeProvider,
    ILogger<SapAvailabilityProbe> logger)
{
    public async Task<SapProbeRun> RunAsync(SapProbeRun run, CancellationToken cancellationToken)
    {
        var options = settings.Value;
        var open = await db.SapOutages
            .Where(outage => outage.EndedAtUtc == null)
            .OrderBy(outage => outage.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (circuit.IsSwitchedOff)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (open is null)
            {
                open = new SapOutageEntity
                {
                    StartedAtUtc = now,
                    DeclaredAtUtc = now,
                    Cause = SapOutageCauses.SwitchedOff,
                    FirstError = "The SAP connection was turned off in Settings.",
                    LastProbeAtUtc = now
                };
                db.SapOutages.Add(open);
                logger.LogWarning("SAP connection is turned off in Settings; recording it as an outage.");
            }
            else
            {
                open.LastProbeAtUtc = now;
            }

            await db.SaveChangesAsync(CancellationToken.None);
            availability.Apply(SapAvailability.StateOf(open));
            return SapProbeRun.None;
        }

        var (answered, error) = await ProbeAsync(options, cancellationToken);
        var probedAt = timeProvider.GetUtcNow().UtcDateTime;

        run = answered ? run.Succeeded(probedAt) : run.Failed(probedAt, error!);

        if (open is not null)
        {
            open.LastProbeAtUtc = probedAt;

            if (answered && run.Successes >= Math.Max(1, options.SuccessesToEnd))
            {
                open.EndedAtUtc = run.RunStartedAtUtc;
                logger.LogWarning(
                    "SAP is available again. Outage {OutageId} ({Cause}) ran from {StartedAt:u} to {EndedAt:u}, {FailedProbes} failed probe(s).",
                    open.Id, open.Cause, open.StartedAtUtc, open.EndedAtUtc, open.FailedProbes);
            }
            else if (!answered)
            {
                open.FailedProbes++;
                open.LastError = Truncate(error, 1000);
            }
        }
        else if (!answered && run.Failures >= Math.Max(1, options.FailuresToDeclare))
        {
            open = new SapOutageEntity
            {
                StartedAtUtc = run.RunStartedAtUtc,
                DeclaredAtUtc = probedAt,
                Cause = SapOutageCauses.Unreachable,
                FirstError = Truncate(run.FirstError, 1000),
                LastError = Truncate(error, 1000),
                FailedProbes = run.Failures,
                LastProbeAtUtc = probedAt
            };
            db.SapOutages.Add(open);
            logger.LogWarning(
                "SAP is unavailable: {Failures} probes failed in a row since {StartedAt:u}. Latest: {Error}",
                run.Failures, run.RunStartedAtUtc, error);
        }

        if (db.ChangeTracker.HasChanges())
        {
            // Not the job's token: an outage that is found and then not written down is the one thing
            // this class exists to prevent.
            await db.SaveChangesAsync(CancellationToken.None);
            availability.Apply(SapAvailability.StateOf(open));
        }

        return run;
    }

    private async Task<(bool Answered, string? Error)> ProbeAsync(
        SapAvailabilitySettings options,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.ProbeTimeoutSeconds));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        // Interactive, so a probe is not queued behind the background passes that fill four of the six
        // SAP slots after an outage. A probe that times out waiting for a slot would declare SAP down
        // because the app was busy talking to it.
        using var priority = SapRequestPriority.BeginInteractive();

        try
        {
            await sap.PingAsync(budget.Token);
            return (true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, $"SAP did not answer the probe within {timeout.TotalSeconds:0}s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, $"{ex.GetType().Name}: {ex.GetBaseException().Message}");
        }
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>
/// The run of like probe results so far: consecutive failures or consecutive successes, never both.
/// </summary>
/// <param name="Failures">Failed probes in a row.</param>
/// <param name="Successes">Successful probes in a row.</param>
/// <param name="RunStartedAtUtc">When the current run's first probe happened.</param>
/// <param name="FirstError">What the first failure of a failing run was told.</param>
public sealed record SapProbeRun(int Failures, int Successes, DateTime RunStartedAtUtc, string? FirstError)
{
    public static readonly SapProbeRun None = new(0, 0, default, null);

    public SapProbeRun Succeeded(DateTime atUtc) =>
        Successes > 0 ? this with { Successes = Successes + 1 } : new SapProbeRun(0, 1, atUtc, null);

    public SapProbeRun Failed(DateTime atUtc, string error) =>
        Failures > 0 ? this with { Failures = Failures + 1 } : new SapProbeRun(1, 0, atUtc, error);
}
