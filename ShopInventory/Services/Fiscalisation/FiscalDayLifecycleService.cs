using System.Globalization;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// Carries each device's fiscal day the rest of the way to ZIMRA.
///
/// A receipt a handset signed and the platform archived is not with ZIMRA. It is with ZIMRA when the day
/// it belongs to is closed, packaged into an offline file, and that file is uploaded and accepted. Until
/// this ran, those last steps were a person clicking through the platform's own console, which meant that
/// on any day nobody clicked, a van's takings existed everywhere except at the revenue authority.
///
/// <para><b>The sequence.</b> One row per device per day in <c>FiscalDayStates</c>, advanced a step at a
/// time and resumed from whatever step it reached:</para>
/// <list type="bullet">
///   <item><see cref="FiscalDayLifecycleStatus.Open"/> — trading. Receipts are still arriving.</item>
///   <item><see cref="FiscalDayLifecycleStatus.Drained"/> — every receipt this application holds for the
///     day has been archived by the platform. No call is made to reach this; it is a fact about our own
///     rows, and it is the gate everything else waits behind.</item>
///   <item><see cref="FiscalDayLifecycleStatus.Closed"/> — closed at FDMS by <c>POST /api/fiscal-day/close</c>.
///     Only an <em>Online</em>-mode device passes through here, and for one it is the finish line: its
///     receipts went to FDMS one at a time as they were signed, so there is no file to package.</item>
///   <item><see cref="FiscalDayLifecycleStatus.FileGenerated"/> — the day's receipts are packaged. An
///     <em>Offline</em>-mode device — which is every van handset — reaches this straight from
///     <c>Drained</c>, because the platform refuses <c>CloseDay</c> for an offline device outright
///     (<c>OFFLINE_DEVICE_CLOSE_BLOCKED</c>): its close travels as a declaration inside the last file,
///     carrying the day's counters and their device signature, and becomes real when that file lands.</item>
///   <item><see cref="FiscalDayLifecycleStatus.Submitted"/> — uploaded and accepted. ZIMRA has the day.</item>
/// </list>
///
/// <para><b>The rule that overrides every other consideration:</b> a day is never closed while the device
/// still has a signed receipt this application has not handed over. Those receipt numbers are inside the
/// day. Once the day is closed FDMS will not accept them, and no later file can carry them — the customer
/// keeps a fiscal receipt for a sale the revenue authority is never told about, permanently. A day that
/// cannot close is a visible problem; a day closed over a missing receipt is an invisible one.</para>
///
/// <para>Which is why that check is deliberately wider than "receipts stamped with this device and this
/// day number". A receipt can consume a chain number and still arrive with no day number, or with a device
/// string that does not parse — the handset contract only guarantees a number was spent, and a row keyed
/// on neither column is invisible to a query keyed on both. So the gate asks three questions: what this
/// day still owes, what the whole device still owes as a hole in its chain whatever day it claims, and
/// whether any receipt anywhere carries a chain number it cannot be attributed to a device by. The last
/// one holds every device, because until a person says which device it belongs to, it might be this
/// one.</para>
///
/// <para><b>Indeterminate is not failure.</b> A close or an upload that comes back without saying whether
/// it took effect lands in <see cref="FiscalDayLifecycleStatus.NeedsReconciliation"/> and is resolved by
/// <em>reading</em> — the device's FDMS status, or the list of files FDMS has accepted — never by doing it
/// again. Closing a fiscal day twice, or uploading one file twice, is not idempotent at FDMS.</para>
/// </summary>
public sealed class FiscalDayLifecycleService(
    ApplicationDbContext context,
    IFiscalDayAdminApiClient adminClient,
    IFiscalisationApiClient platformClient,
    IFiscalDeviceConfigCache deviceConfigCache,
    IOptions<FiscalisationSettings> fiscalisationOptions,
    ILogger<FiscalDayLifecycleService> logger)
{
    /// <summary>
    /// How far back sales are read when discovering which device-days exist.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. A day only leaves this window once it is finished, because an unfinished
    /// <c>FiscalDayStates</c> row is picked up regardless of how old its sales are — the window governs
    /// discovery, not resumption. It has to outlast a handset that was out of signal over a long weekend.
    /// </remarks>
    private const int DiscoveryLookbackDays = 21;

    /// <summary>
    /// After this many attempts a day stops being advanced automatically and waits for a person.
    /// </summary>
    /// <remarks>
    /// Every attempt past the first is one the previous one already refused for a stated reason, and the
    /// reasons the platform gives here — a counter gap, an expired certificate, a partial day — are not
    /// ones that pass on their own. Retrying past this point writes a log line an hour and fixes nothing.
    /// </remarks>
    private const int MaxAdvanceAttempts = 6;

    /// <summary>The mode string the platform reports for a device that signs its own receipts.</summary>
    private const string OfflineOperatingMode = "Offline";

    /// <summary>
    /// The platform's wording for "everything in this day is already inside a file". It is a refusal, but
    /// the thing it refuses is already done, so it means the day has moved on rather than gone wrong.
    /// </summary>
    private const string NothingLeftToPackageMarker = "none are still waiting to be packaged";

    /// <summary>FDMS's verdict on a file it processed without complaint.</summary>
    private const string FileProcessedSuccessfully = "CompleteSuccessful";

    private readonly FiscalisationSettings _settings = fiscalisationOptions.Value;

    public Task<FiscalDayLifecycleRunResult> AdvanceDueDaysAsync(CancellationToken cancellationToken = default)
        => AdvanceDueDaysAsync(DateTime.UtcNow, cancellationToken);

    /// <summary>
    /// The same pass against a stated instant.
    /// </summary>
    /// <remarks>
    /// Every decision here is a comparison against the clock — whether the close time has passed, whether
    /// the day is over its permitted hours, whether the warning is due — so a test that cannot state the
    /// time can only assert what happens to be true when it runs.
    /// </remarks>
    internal async Task<FiscalDayLifecycleRunResult> AdvanceDueDaysAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var result = new FiscalDayLifecycleRunResult();

        if (!_settings.Enabled)
        {
            logger.LogDebug("Fiscalisation is switched off, so no fiscal day was advanced.");
            result.Skipped = true;
            return result;
        }

        if (!adminClient.IsConfigured)
        {
            // One line naming the cause, not one per device: nothing can move until this is set, and there
            // is exactly one thing to do about it.
            result.ServiceAccountMissing = true;
            return result;
        }

        var nowLocal = AuditService.ToCAT(nowUtc);

        var snapshots = await ReadReceiptSnapshotsAsync(nowUtc, cancellationToken);
        var states = await SynchroniseStatesAsync(snapshots, nowUtc, cancellationToken);

        result.DaysTracked = states.Count;

        // A device's chain holes are counted once however many of its days are blocked by them, so the
        // reported figure is receipts rather than receipts times days.
        var devicesWithCountedChainHoles = new HashSet<int>();

        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                try
                {
                    // Without the snapshot: it is discovery and scheduling only. What the day still owes is
                    // re-read inside the step, immediately before anything irreversible.
                    await AdvanceOneAsync(
                        state, nowUtc, nowLocal, result, devicesWithCountedChainHoles, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One device's day never stops another's. A van's receipts are its own chain and its own
                    // deadline.
                    RecordFailure(state, ex.Message, nowUtc, result);
                    logger.LogError(
                        ex,
                        "Advancing fiscal day {FiscalDayNo} on device {DeviceId} failed.",
                        state.FiscalDayNo,
                        state.DeviceId);
                }
            }
            finally
            {
                // Saved per device rather than once at the end, following the precedent
                // VanSalesSignedReceiptIngestService states in its own comment, and here it is not merely
                // tidiness: the transition this pass just wrote may be NeedsReconciliation for a file FDMS
                // might already hold. Losing that write — a crash, a shutdown, the next device throwing —
                // puts the day back at FileGenerated, and the next pass uploads a file that must never be
                // sent twice.
                //
                // With CancellationToken.None deliberately. The operation whose outcome is being recorded
                // has already happened at the platform; a host shutting down is exactly when the record of
                // it matters most, and a save that abandons itself on the same token leaves the day
                // claiming a step it never reached.
                await context.SaveChangesAsync(CancellationToken.None);
            }
        }

        // Only the discovery writes are left by now — a day already finished whose ingested count moved, on
        // a pass where no state was in flight to carry it. Everything a step touched was saved above.
        await context.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Puts a refused day back into the automatic sequence, once a person has dealt with what refused it.
    /// </summary>
    /// <remarks>
    /// <see cref="FiscalDayLifecycleStatus.Failed"/> means FDMS itself said no to this day's file, and
    /// nothing automatic touches it again — deliberately, because the only automated response available is
    /// to package and upload once more, and FDMS already holds the receipts of the file it rejected.
    /// Someone has to rebuild the day on the platform's Offline Files page first. This is the door back
    /// afterwards, and without it a refusal is permanent and the day's receipts never reach ZIMRA.
    ///
    /// It resumes at the furthest step the day actually reached, which is the same rule the pass itself
    /// follows: a day whose file was generated picks up at the upload, one that never got that far at the
    /// close. The outstanding-receipt gate is re-run either way before anything is sent.
    /// </remarks>
    /// <returns>False when there is no such day, or it is not in <c>Failed</c> — nothing else is resumable.</returns>
    public Task<bool> ResumeFailedDayAsync(
        int deviceId,
        int fiscalDayNo,
        string? resumedBy,
        CancellationToken cancellationToken = default)
        => ResumeFailedDayAsync(deviceId, fiscalDayNo, resumedBy, DateTime.UtcNow, cancellationToken);

    internal async Task<bool> ResumeFailedDayAsync(
        int deviceId,
        int fiscalDayNo,
        string? resumedBy,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var state = await context.FiscalDayStates
            .FirstOrDefaultAsync(
                day => day.DeviceId == deviceId && day.FiscalDayNo == fiscalDayNo, cancellationToken);

        if (state is null || state.Status != FiscalDayLifecycleStatus.Failed)
        {
            return false;
        }

        var refusal = state.LastError;

        state.Status = state.FileGeneratedAtUtc is null
            ? FiscalDayLifecycleStatus.Drained
            : FiscalDayLifecycleStatus.FileGenerated;
        state.Attempts = 0;
        state.LastError = Truncate(
            $"Put back for another attempt by {resumedBy ?? "an operator"}. It had been refused: {refusal}", 2000);
        Touch(state, nowUtc);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Fiscal day {FiscalDayNo} on device {DeviceId} was put back into the automatic sequence at {Status} by "
            + "{ResumedBy}. It had been refused: {Refusal}",
            fiscalDayNo,
            deviceId,
            state.Status,
            resumedBy ?? "an operator",
            refusal);

        return true;
    }

    // ── Discovery ───────────────────────────────────────────────────────────

    /// <summary>
    /// What this application knows about each device-day: when the handset opened it, how many of its
    /// receipts the platform has taken, and how many are still owed.
    /// </summary>
    /// <remarks>
    /// <see cref="DesktopSaleReceiptIngestStatus.Unstamped"/> is counted as settled rather than owed, and
    /// that is the whole reason it exists as its own status. An unstamped sale consumed no receipt number
    /// on the device, so nothing in the chain is waiting behind it and the day can close over it — whereas
    /// treating it as owed would stop the day of every van still on a pre-signing build.
    /// </remarks>
    private async Task<Dictionary<(int DeviceId, int FiscalDayNo), FiscalDayReceiptSnapshot>>
        ReadReceiptSnapshotsAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var lookbackStart = nowUtc.Date.AddDays(-DiscoveryLookbackDays);

        var grouped = await context.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.FiscalDeviceId != null
                           && sale.FiscalDayNo != null
                           && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.NotApplicable
                           && sale.DocDate >= lookbackStart)
            .GroupBy(sale => new { sale.FiscalDeviceId, sale.FiscalDayNo })
            .Select(group => new
            {
                group.Key.FiscalDeviceId,
                group.Key.FiscalDayNo,
                OpenedAtLocal = group.Min(sale => sale.FiscalDayOpenedAt),
                Ingested = group.Count(sale =>
                    sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Ingested),
                Outstanding = group.Count(sale =>
                    sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Ingested
                    && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Unstamped)
            })
            .ToListAsync(cancellationToken);

        var snapshots = new Dictionary<(int, int), FiscalDayReceiptSnapshot>();

        foreach (var row in grouped)
        {
            if (row.FiscalDeviceId is not { } deviceId
                || !int.TryParse(row.FiscalDayNo, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fiscalDayNo)
                || fiscalDayNo <= 0)
            {
                logger.LogWarning(
                    "Ignoring van receipts on device {DeviceId} with fiscal day '{FiscalDayNo}': the day number is not a "
                    + "positive integer, so the day cannot be closed or packaged.",
                    row.FiscalDeviceId,
                    row.FiscalDayNo);
                continue;
            }

            var key = (deviceId, fiscalDayNo);

            if (snapshots.TryGetValue(key, out var existing))
            {
                // The grouping is on the stored string but the key is the parsed number, and the parse
                // accepts "7", " 7" and "+7" as the same day. Those are three groups arriving here for one
                // key: assigning would keep the last and silently drop the others' receipts — including
                // their outstanding ones, which is the count that decides whether the day may close.
                snapshots[key] = existing with
                {
                    OpenedAtLocal = EarliestOf(existing.OpenedAtLocal, row.OpenedAtLocal),
                    Ingested = existing.Ingested + row.Ingested,
                    Outstanding = existing.Outstanding + row.Outstanding
                };

                logger.LogWarning(
                    "Device {DeviceId} has receipts recorded against fiscal day '{FiscalDayNo}' in more than one "
                    + "written form. They are the same day {ParsedFiscalDayNo} and have been counted together.",
                    deviceId,
                    row.FiscalDayNo,
                    fiscalDayNo);

                continue;
            }

            snapshots[key] = new FiscalDayReceiptSnapshot(
                deviceId, fiscalDayNo, row.OpenedAtLocal, row.Ingested, row.Outstanding);
        }

        return snapshots;
    }

    private static DateTime? EarliestOf(DateTime? left, DateTime? right)
        => left is null ? right : right is null ? left : left < right ? left : right;

    /// <summary>
    /// Brings <c>FiscalDayStates</c> level with what the sales say, and returns the rows still in flight.
    /// </summary>
    /// <remarks>
    /// Unfinished rows are loaded whether or not any sale still falls inside the discovery window. A day
    /// that stalled a month ago is exactly the one that must not be forgotten, and forgetting it is what a
    /// window-only query would do.
    /// </remarks>
    private async Task<List<FiscalDayStateEntity>> SynchroniseStatesAsync(
        Dictionary<(int DeviceId, int FiscalDayNo), FiscalDayReceiptSnapshot> snapshots,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var deviceIds = snapshots.Keys.Select(key => key.DeviceId).Distinct().ToList();
        var fiscalDayNos = snapshots.Keys.Select(key => key.FiscalDayNo).Distinct().ToList();

        var known = await context.FiscalDayStates
            .Where(state => (deviceIds.Contains(state.DeviceId) && fiscalDayNos.Contains(state.FiscalDayNo))
                            || (state.Status != FiscalDayLifecycleStatus.Submitted
                                && state.Status != FiscalDayLifecycleStatus.Closed
                                && state.Status != FiscalDayLifecycleStatus.Failed))
            .ToListAsync(cancellationToken);

        var byKey = known.ToDictionary(state => (state.DeviceId, state.FiscalDayNo));

        foreach (var snapshot in snapshots.Values)
        {
            if (!byKey.TryGetValue((snapshot.DeviceId, snapshot.FiscalDayNo), out var state))
            {
                state = new FiscalDayStateEntity
                {
                    DeviceId = snapshot.DeviceId,
                    FiscalDayNo = snapshot.FiscalDayNo,
                    OpenedAtLocal = snapshot.OpenedAtLocal,
                    Status = FiscalDayLifecycleStatus.Open,
                    CreatedAt = nowUtc,
                    UpdatedAt = nowUtc
                };

                context.FiscalDayStates.Add(state);
                byKey[(snapshot.DeviceId, snapshot.FiscalDayNo)] = state;
                known.Add(state);
            }

            // The opening time comes from the handset and is the only source that survives the next day
            // being opened, so it is filled in whenever it is still missing.
            state.OpenedAtLocal ??= snapshot.OpenedAtLocal;
            state.IngestedReceiptCount = snapshot.Ingested;
        }

        return known
            .Where(state => state.Status
                is FiscalDayLifecycleStatus.Open
                or FiscalDayLifecycleStatus.Drained
                or FiscalDayLifecycleStatus.FileGenerated
                or FiscalDayLifecycleStatus.NeedsReconciliation)
            .OrderBy(state => state.DeviceId)
            .ThenBy(state => state.FiscalDayNo)
            .ToList();
    }

    // ── One day, one step at a time ─────────────────────────────────────────

    private async Task AdvanceOneAsync(
        FiscalDayStateEntity state,
        DateTime nowUtc,
        DateTime nowLocal,
        FiscalDayLifecycleRunResult result,
        HashSet<int> devicesWithCountedChainHoles,
        CancellationToken cancellationToken)
    {
        if (!adminClient.IsConfiguredForDevice(state.DeviceId))
        {
            // The platform reads one FdmsDeviceId claim off the token, so a fleet needs a credential per
            // device. Saying so here costs nothing; sending anyway costs six attempts against a bodyless
            // 403 and a parked day.
            result.DevicesWithoutServiceAccount++;
            RecordFailure(
                state,
                $"No platform service account is configured for device {state.DeviceId}. The platform authorises "
                + "each fiscal-day call against the device the request names, so one account cannot serve the "
                + $"fleet: set Fiscalisation__FiscalDay__DeviceServiceAccounts__{state.DeviceId}__Username and "
                + "__Password. Nothing was sent.",
                nowUtc,
                result);
            return;
        }

        var config = await deviceConfigCache.TryGetAsync(state.DeviceId, cancellationToken);

        if (config is null)
        {
            // Without the device's configuration neither the day's limit nor its operating mode is known,
            // and guessing the mode picks between two irreversible operations. Wait for the next pass.
            result.Errors.Add(
                $"Device {state.DeviceId}: the platform could not be asked for its configuration, so fiscal day "
                + $"{state.FiscalDayNo} was left where it is.");
            return;
        }

        if (config.TaxPayerDayMaxHrs > 0)
        {
            state.MaxDurationHours ??= config.TaxPayerDayMaxHrs;
        }

        RaiseDurationWarningIfDue(state, nowLocal, result);

        if (state.Status == FiscalDayLifecycleStatus.NeedsReconciliation)
        {
            await ResolveReconciliationAsync(state, config, nowUtc, nowLocal, result, cancellationToken);
            return;
        }

        if (!HasAttemptBudget(state, nowUtc, result))
        {
            return;
        }

        var isOffline = string.Equals(
            config.DeviceOperatingMode, OfflineOperatingMode, StringComparison.OrdinalIgnoreCase);

        if (state.Status is FiscalDayLifecycleStatus.Open
            or FiscalDayLifecycleStatus.Drained
            or FiscalDayLifecycleStatus.FileGenerated)
        {
            // Counted here rather than taken from the snapshot read at the top of the pass, and counted
            // again on a resumed day, because this is the last instant before something irreversible.
            //
            // The snapshot is a scheduling aid and cannot be the gate: it is filtered to the discovery
            // window, so a day with sales either side of it would close over the difference, and it was
            // taken before the network calls this pass has since made, so a receipt ingested while an
            // earlier device was being closed would be invisible to it.
            var outstanding = await CountOutstandingReceiptsAsync(state, cancellationToken);

            if (outstanding.Total > 0)
            {
                BlockOnOutstandingReceipts(state, outstanding, nowUtc, result, devicesWithCountedChainHoles);
                return;
            }

            if (state.Status == FiscalDayLifecycleStatus.Open)
            {
                if (!IsDueToClose(state, nowLocal))
                {
                    return;
                }

                state.Status = FiscalDayLifecycleStatus.Drained;
                state.LastError = null;
                Touch(state, nowUtc);
                result.DaysAdvanced++;
            }
        }

        switch (state.Status)
        {
            case FiscalDayLifecycleStatus.Drained when !isOffline:
                await CloseAtFdmsAsync(state, nowUtc, result, cancellationToken);
                break;

            case FiscalDayLifecycleStatus.Drained:
            case FiscalDayLifecycleStatus.FileGenerated:
                await PackageAndUploadAsync(state, nowUtc, result, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Whether the day may be advanced again, and gives it a fresh budget once one has expired.
    /// </summary>
    /// <remarks>
    /// The budget exists because most reasons a day is refused — a counter gap, an expired certificate, a
    /// partial day — do not pass on their own, and retrying hourly writes a log line an hour and fixes
    /// nothing. But the commonest way a day burns six attempts is a platform outage, and that is a
    /// condition which clears. Parking the day permanently for it meant its receipts never reached ZIMRA
    /// at all, which is a worse outcome than a noisy log.
    ///
    /// So the budget pauses rather than ends: the day is left alone for
    /// <see cref="FiscalDaySettings.RetryBudgetCooldownHours"/>, then given another set of attempts. The
    /// pause is measured from <see cref="FiscalDayStateEntity.UpdatedAt"/>, which is why nothing in this
    /// pass touches a day it did not actually change.
    /// </remarks>
    private bool HasAttemptBudget(
        FiscalDayStateEntity state,
        DateTime nowUtc,
        FiscalDayLifecycleRunResult result)
    {
        if (state.Attempts < MaxAdvanceAttempts)
        {
            return true;
        }

        var cooldown = TimeSpan.FromHours(Math.Max(1, _settings.FiscalDay.RetryBudgetCooldownHours));
        var resumesAtUtc = state.UpdatedAt + cooldown;

        if (nowUtc < resumesAtUtc)
        {
            result.DaysAwaitingRetryBudget++;
            result.Errors.Add(
                $"Device {state.DeviceId}, fiscal day {state.FiscalDayNo}: refused {state.Attempts} time(s), so it "
                + $"is paused until {resumesAtUtc:yyyy-MM-dd HH:mm}Z. Last reason: {state.LastError}");

            // Deliberately not touched. UpdatedAt is what the pause is measured from, and a pass that
            // stamped it on the way past would hold the day paused for as long as the passes keep coming.
            return false;
        }

        logger.LogInformation(
            "Fiscal day {FiscalDayNo} on device {DeviceId} was refused {Attempts} time(s) and paused; the pause has "
            + "run out, so it is being tried again. Last reason: {LastError}",
            state.FiscalDayNo,
            state.DeviceId,
            state.Attempts,
            state.LastError);

        state.Attempts = 0;
        Touch(state, nowUtc);
        return true;
    }

    /// <summary>
    /// Holds the day open and says which of the three kinds of outstanding receipt is doing it.
    /// </summary>
    /// <remarks>
    /// Distinctly, because the three need different people. This day's own arrears drain on their own once
    /// the platform is reachable; a chain hole on the device needs someone to look at one receipt; a
    /// receipt with a chain number and no device cannot be attributed at all and is holding every device
    /// on the estate, which is worth saying out loud rather than adding to a total.
    /// </remarks>
    private void BlockOnOutstandingReceipts(
        FiscalDayStateEntity state,
        OutstandingReceipts outstanding,
        DateTime nowUtc,
        FiscalDayLifecycleRunResult result,
        HashSet<int> devicesWithCountedChainHoles)
    {
        var reasons = new List<string>();

        if (outstanding.ForThisDay > 0)
        {
            reasons.Add(
                $"{outstanding.ForThisDay} signed receipt(s) from this device have not reached the platform");
        }

        if (outstanding.ChainHoles > 0)
        {
            reasons.Add(
                $"{outstanding.ChainHoles} receipt(s) on this device took a chain number and can never be submitted "
                + "as they stand, whatever fiscal day they name");
        }

        if (outstanding.Unattributable > 0)
        {
            reasons.Add(
                $"{outstanding.Unattributable} receipt(s) took a chain number but name no device, so they cannot be "
                + "attributed and hold every device until a person resolves them");
        }

        var message = string.Join("; ", reasons)
                      + ". The day cannot be closed without stranding them.";

        // Only touched if something actually changed: UpdatedAt is read as "when this day last moved", by
        // the exception center's clock and by the attempt-budget pause.
        var changed = state.Status != FiscalDayLifecycleStatus.Open
                      || !string.Equals(state.LastError, message, StringComparison.Ordinal);

        // The absolute rule. Their numbers are inside this day; closing it strands them for good.
        state.Status = FiscalDayLifecycleStatus.Open;
        state.LastError = Truncate(message, 2000);

        if (changed)
        {
            Touch(state, nowUtc);
        }

        result.DaysBlockedByOutstandingReceipts++;
        result.OutstandingReceipts += outstanding.ForThisDay;

        if (outstanding.ChainHoles > 0 && devicesWithCountedChainHoles.Add(state.DeviceId))
        {
            result.ChainHoleReceipts += outstanding.ChainHoles;
        }

        // One estate-wide figure, not a sum over the days it blocked.
        result.UnattributableReceipts = Math.Max(result.UnattributableReceipts, outstanding.Unattributable);
    }

    /// <summary>
    /// Whether the day should now be closed.
    /// </summary>
    /// <remarks>
    /// Three ways to be due, in order of how much choice there is about it. Past the taxpayer's maximum day
    /// length ZIMRA will refuse the day, so that one is not a preference. A day whose opening date is behind
    /// today's is finished by definition. Today's own day waits for the configured close time, which is what
    /// stops a mid-morning pass closing a van that is still out.
    /// </remarks>
    private bool IsDueToClose(FiscalDayStateEntity state, DateTime nowLocal)
    {
        if (state.OpenedAtLocal is not { } openedAtLocal)
        {
            // Nothing to measure against. The handset tells us when it opened the day; until one has, there
            // is no defensible moment to close it.
            return false;
        }

        if (state.MaxDurationHours is > 0
            && (nowLocal - openedAtLocal).TotalHours >= state.MaxDurationHours.Value)
        {
            return true;
        }

        if (openedAtLocal.Date < nowLocal.Date)
        {
            return true;
        }

        return TimeOnly.FromDateTime(nowLocal) >= ParseCloseAtLocalTime(_settings.FiscalDay.CloseAtLocalTime);
    }

    /// <summary>
    /// Says, once, that a day is running out of time.
    /// </summary>
    /// <remarks>
    /// Once per day, not once per pass: the lifecycle is walked far more often than a warning should be
    /// repeated, and an alert that reappears every hour is one people learn to dismiss without reading.
    /// </remarks>
    private void RaiseDurationWarningIfDue(
        FiscalDayStateEntity state,
        DateTime nowLocal,
        FiscalDayLifecycleRunResult result)
    {
        if (state.DurationWarningRaised
            || state.OpenedAtLocal is not { } openedAtLocal
            || state.MaxDurationHours is not { } maxHours
            || maxHours <= 0)
        {
            return;
        }

        var warnAtHours = maxHours * Math.Clamp(_settings.FiscalDay.WarnAtPercentOfMaxHrs, 1, 100) / 100.0;
        var elapsedHours = (nowLocal - openedAtLocal).TotalHours;

        if (elapsedHours < warnAtHours)
        {
            return;
        }

        state.DurationWarningRaised = true;
        result.DurationWarnings++;

        logger.LogWarning(
            "Fiscal day {FiscalDayNo} on device {DeviceId} has been open {ElapsedHours:F1} of the taxpayer's "
            + "{MaxHours} permitted hours. ZIMRA refuses a day that runs over, so it has to be closed before "
            + "{DeadlineLocal:yyyy-MM-dd HH:mm}.",
            state.FiscalDayNo,
            state.DeviceId,
            elapsedHours,
            maxHours,
            openedAtLocal.AddHours(maxHours));
    }

    // ── The platform operations ─────────────────────────────────────────────

    /// <summary>Closes an Online-mode device's day at FDMS. Its receipts are already there one by one.</summary>
    private async Task CloseAtFdmsAsync(
        FiscalDayStateEntity state,
        DateTime nowUtc,
        FiscalDayLifecycleRunResult result,
        CancellationToken cancellationToken)
    {
        state.Attempts++;

        try
        {
            await adminClient.CloseFiscalDayAsync(
                new CloseDayApiRequest { DeviceId = state.DeviceId, FiscalDayNo = state.FiscalDayNo },
                cancellationToken);

            state.Status = FiscalDayLifecycleStatus.Closed;
            state.ClosedAtUtc = nowUtc;
            state.LastError = null;
            Touch(state, nowUtc);

            result.DaysAdvanced++;
            result.DaysClosed++;

            logger.LogInformation(
                "Closed fiscal day {FiscalDayNo} on device {DeviceId} at FDMS.", state.FiscalDayNo, state.DeviceId);
        }
        catch (FiscalisationApiException ex) when (ex.RequiresReconciliation)
        {
            MarkNeedsReconciliation(state, ex.Message, nowUtc, result);
        }
        catch (Exception ex) when (IsTransportIndeterminate(ex))
        {
            // Ahead of the plain FiscalisationApiException arm on purpose: a 503 is one of these, and
            // falling through to RecordFailure would leave the day at Drained for the next pass to close a
            // second time. The close request may have reached FDMS.
            MarkNeedsReconciliation(state, DescribeTransportIndeterminate(ex, "close"), nowUtc, result);
        }
        catch (FiscalisationApiException ex)
        {
            RecordFailure(state, ex.Message, nowUtc, result);
        }
    }

    /// <summary>
    /// Packages the day's receipts with the close declared in the last file, then uploads them.
    /// </summary>
    /// <remarks>
    /// One method for both the first attempt and a resumed one, because generation is a read on the
    /// platform's side — it reserves nothing and tells FDMS nothing — so a run that died after generating
    /// simply asks again rather than needing the bytes kept here. Asking again also re-selects the
    /// receipts: any already carried by an uploaded file drop out, so the second pass produces the
    /// remainder rather than a duplicate of what FDMS already holds.
    /// </remarks>
    private async Task PackageAndUploadAsync(
        FiscalDayStateEntity state,
        DateTime nowUtc,
        FiscalDayLifecycleRunResult result,
        CancellationToken cancellationToken)
    {
        state.Attempts++;

        GenerateOfflineFileApiResponse generated;

        try
        {
            generated = await adminClient.GenerateOfflineFileAsync(
                new GenerateOfflineFileApiRequest
                {
                    DeviceId = state.DeviceId,
                    FiscalDayNo = state.FiscalDayNo,
                    CloseFiscalDay = true,

                    // Null for every device the platform signs for, which is all of them except a van
                    // handset. A handset's key lives in its own keystore, so the platform can verify the
                    // close it signed but never produce one — send null for such a device and its day
                    // cannot be closed at all.
                    DeclaredClose = ReadDeclaredClose(state)
                },
                cancellationToken);
        }
        catch (FiscalisationApiException ex) when (
            ex.Message.Contains(NothingLeftToPackageMarker, StringComparison.OrdinalIgnoreCase))
        {
            // Everything is already inside a file that was uploaded. Nothing left to generate and nothing to
            // repeat — only something to confirm.
            MarkNeedsReconciliation(
                state,
                "Every receipt for this day is already inside an uploaded file; confirming with FDMS what it "
                + "made of them.",
                nowUtc,
                result);
            return;
        }
        catch (FiscalisationApiException ex)
        {
            RecordFailure(state, ex.Message, nowUtc, result);
            return;
        }

        if (generated.Files.Count == 0)
        {
            RecordFailure(state, "The platform generated no offline file for this day.", nowUtc, result);
            return;
        }

        foreach (var warning in generated.Warnings)
        {
            logger.LogInformation(
                "Packaging fiscal day {FiscalDayNo} on device {DeviceId}: {Warning}",
                state.FiscalDayNo,
                state.DeviceId,
                warning);
        }

        if (state.Status != FiscalDayLifecycleStatus.FileGenerated)
        {
            state.Status = FiscalDayLifecycleStatus.FileGenerated;
            result.DaysAdvanced++;
        }

        state.FileGeneratedAtUtc = nowUtc;
        state.OfflineFileReference = FormatSequences(generated.Files.Select(file => file.FileSequence));
        state.LastError = null;
        Touch(state, nowUtc);

        logger.LogInformation(
            "Packaged fiscal day {FiscalDayNo} on device {DeviceId} into {FileCount} offline file(s) carrying "
            + "{ReceiptCount} receipt(s); the close is declared in sequence {ClosingSequence}.",
            state.FiscalDayNo,
            state.DeviceId,
            generated.Files.Count,
            generated.Files.Sum(file => file.ReceiptCount),
            generated.Files.LastOrDefault(file => file.DeclaresFiscalDayClose)?.FileSequence);

        await UploadAsync(state, generated, nowUtc, result, cancellationToken);
    }

    /// <summary>
    /// Uploads the day's files in sequence order.
    /// </summary>
    /// <remarks>
    /// In order, and stopping at the first one that does not come back settled, because the last file is the
    /// one carrying the close declaration — uploading it over a gap would declare a day whose counters do not
    /// match the receipts FDMS holds.
    /// </remarks>
    /// <summary>
    /// The close a handset signed for this day, or null when none was held.
    /// </summary>
    /// <remarks>
    /// Null is the normal answer and means "platform, sign this yourself" — which is right for every
    /// device whose key the platform holds. It is only wrong for a van handset, and there the platform
    /// refuses the close rather than signing a wrong one, so a missing close surfaces as a day that will
    /// not package instead of a day closed on totals nobody signed.
    ///
    /// A stored value that will not parse is treated as absent and logged rather than thrown. The
    /// alternative is a lifecycle run that dies on one malformed row and stops advancing every other
    /// device's day.
    /// </remarks>
    private DeclaredFiscalDayCloseApiRequest? ReadDeclaredClose(FiscalDayStateEntity state)
    {
        if (string.IsNullOrWhiteSpace(state.DeclaredCloseJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeclaredFiscalDayCloseApiRequest>(state.DeclaredCloseJson);
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "The signed close held for device {DeviceId}, fiscal day {FiscalDayNo} could not be read, so the "
                + "day will be offered without it and the platform will refuse to close it. The handset must "
                + "re-send.",
                state.DeviceId,
                state.FiscalDayNo);

            return null;
        }
    }

    private async Task UploadAsync(
        FiscalDayStateEntity state,
        GenerateOfflineFileApiResponse generated,
        DateTime nowUtc,
        FiscalDayLifecycleRunResult result,
        CancellationToken cancellationToken)
    {
        SubmitOfflineFileApiResponse? last = null;

        foreach (var file in generated.Files.OrderBy(file => file.FileSequence))
        {
            SubmitOfflineFileApiResponse submitted;

            try
            {
                submitted = await adminClient.SubmitOfflineFileAsync(
                    new SubmitOfflineFileApiRequest
                    {
                        DeviceId = state.DeviceId,
                        FileName = file.FileName,
                        FileJson = file.FileJson,
                        FiscalDayNo = state.FiscalDayNo,
                        FileSequence = file.FileSequence
                    },
                    cancellationToken);
            }
            catch (FiscalisationApiException ex) when (ex.RequiresReconciliation)
            {
                MarkNeedsReconciliation(state, ex.Message, nowUtc, result);
                return;
            }
            catch (Exception ex) when (IsTransportIndeterminate(ex))
            {
                // Ahead of the plain FiscalisationApiException arm on purpose: a 503 is one of these, and
                // falling through to RecordFailure would leave the day at FileGenerated for the next pass
                // to upload the same file again. The file may already be at FDMS.
                MarkNeedsReconciliation(state, DescribeTransportIndeterminate(ex, "upload"), nowUtc, result);
                return;
            }
            catch (FiscalisationApiException ex)
            {
                RecordFailure(state, ex.Message, nowUtc, result);
                return;
            }

            if (submitted.ReconciliationRequired)
            {
                // FDMS took the upload but has not yet said it processed it cleanly. Not a failure, and
                // emphatically not something to send again — a read settles it on a later pass.
                state.OfflineFileReference = Truncate(submitted.OperationID, 100);
                MarkNeedsReconciliation(
                    state,
                    submitted.ReconciliationDetail
                        ?? $"The platform reports the upload as {submitted.Status}.",
                    nowUtc,
                    result);
                return;
            }

            last = submitted;
        }

        state.Status = FiscalDayLifecycleStatus.Submitted;
        state.FileSubmittedAtUtc = nowUtc;
        state.OfflineFileReference = Truncate(last?.OperationID, 100) ?? state.OfflineFileReference;
        state.LastError = null;
        Touch(state, nowUtc);

        result.DaysAdvanced++;
        result.DaysSubmitted++;

        logger.LogInformation(
            "Fiscal day {FiscalDayNo} on device {DeviceId} is with ZIMRA: {FileCount} offline file(s) accepted, "
            + "operation {OperationId}.",
            state.FiscalDayNo,
            state.DeviceId,
            generated.Files.Count,
            last?.OperationID);
    }

    /// <summary>
    /// Whether a failure of an irreversible call leaves its outcome unknown rather than proving it did not
    /// happen.
    /// </summary>
    /// <remarks>
    /// <see cref="FiscalisationApiException.RequiresReconciliation"/> answers the same question, but only
    /// when the platform answered at all: it is the platform telling us it does not know. The commonest way
    /// an outcome becomes unknown is that nobody tells us anything — the request times out, the connection
    /// is reset, or a proxy in front of the platform returns 502/503/504 having already forwarded the
    /// request. Every one of those can mean the close or the upload reached FDMS and the reply was lost.
    ///
    /// Treated as "not sent" — which is what falling through to <see cref="RecordFailure"/> means, since
    /// that deliberately leaves the status alone for the next pass to resume from — the next pass submits
    /// the same file again, or closes the same day again, and neither is idempotent at FDMS.
    ///
    /// A cancellation is in the list for the same reason and not by accident: the request was in flight
    /// when the host went down, so its fate is exactly as unknown as a timeout's.
    ///
    /// Only for <c>close</c> and <c>submit</c>. Generation is a read on the platform's side that reserves
    /// nothing and tells FDMS nothing, so a timeout there is an ordinary retryable failure.
    /// </remarks>
    private static bool IsTransportIndeterminate(Exception exception) => exception switch
    {
        FiscalisationApiException api => api.StatusCode
            is HttpStatusCode.RequestTimeout
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout,
        // TaskCanceledException, which is what HttpClient raises on its own timeout, derives from this.
        OperationCanceledException => true,
        HttpRequestException => true,
        IOException => true,
        SocketException => true,
        _ => false
    };

    private static string DescribeTransportIndeterminate(Exception exception, string operation)
    {
        var cause = exception switch
        {
            FiscalisationApiException api => $"the platform answered HTTP {(int)api.StatusCode} ({api.Message})",
            OperationCanceledException => "the request did not come back before it was abandoned",
            _ => $"the request failed in transport ({exception.Message})"
        };

        return $"The {operation} was sent and {cause}, so whether FDMS acted on it is unknown. It will be settled "
               + $"by reading FDMS, never by sending the {operation} again.";
    }

    // ── Reconciliation: reading, never repeating ────────────────────────────

    /// <summary>
    /// Settles a day whose outcome is unknown by asking what actually happened.
    /// </summary>
    /// <remarks>
    /// Two reads, chosen by which operation was in doubt. A day that never reached
    /// <see cref="FiscalDayStateEntity.FileGeneratedAtUtc"/> was in doubt over its FDMS close, and the
    /// device's own status says whether that close took. Anything later was in doubt over an upload, and
    /// FDMS's list of accepted files says whether it landed.
    ///
    /// Neither read changes anything. When they do not answer, the day stays here and a person picks it up
    /// from the exception center — which is the right outcome, because the only automated alternative is to
    /// repeat an operation FDMS does not accept twice.
    /// </remarks>
    private async Task ResolveReconciliationAsync(
        FiscalDayStateEntity state,
        FiscalConfigApiResponse config,
        DateTime nowUtc,
        DateTime nowLocal,
        FiscalDayLifecycleRunResult result,
        CancellationToken cancellationToken)
    {
        var isOffline = string.Equals(
            config.DeviceOperatingMode, OfflineOperatingMode, StringComparison.OrdinalIgnoreCase);

        if (!isOffline && state.FileGeneratedAtUtc is null)
        {
            await ResolveCloseFromDeviceStatusAsync(state, nowUtc, result, cancellationToken);
        }
        else
        {
            await ResolveUploadFromFileListAsync(state, nowUtc, nowLocal, result, cancellationToken);
        }

        if (state.Status == FiscalDayLifecycleStatus.NeedsReconciliation)
        {
            result.NeedsReconciliation++;
            result.Errors.Add(
                $"Device {state.DeviceId}, fiscal day {state.FiscalDayNo}: still unresolved — {state.LastError}");
        }
    }

    private async Task ResolveCloseFromDeviceStatusAsync(
        FiscalDayStateEntity state,
        DateTime nowUtc,
        FiscalDayLifecycleRunResult result,
        CancellationToken cancellationToken)
    {
        FiscalStatusApiResponse status;

        try
        {
            status = await platformClient.GetFiscalStatusAsync(state.DeviceId, cancellationToken);
        }
        catch (FiscalisationApiException ex)
        {
            state.LastError = Truncate(
                $"The close outcome is still unknown; FDMS status could not be read: {ex.Message}", 2000);
            Touch(state, nowUtc);
            return;
        }

        if (status.FiscalDayNo != state.FiscalDayNo)
        {
            // The read is device-level: it describes whatever day that device currently has, which after a
            // successful close is the next one. Applying it without checking the number is how "FDMS says
            // FiscalDayOpened" — about day 8 — walks day 7 back to Drained and closes it a second time.
            //
            // A zero or absent day number lands here too, and should: it is the device saying it holds no
            // day, which is no more evidence about day 7 than a reading about day 8 is.
            state.LastError = Truncate(
                $"The close outcome is still unknown; FDMS reports fiscal day {status.FiscalDayNo} on this device "
                + $"({status.FiscalDayStatus}), not day {state.FiscalDayNo}, so it says nothing about this day. Check "
                + "the platform's fiscal day history for the device.",
                2000);
            Touch(state, nowUtc);
            return;
        }

        switch (status.FiscalDayStatus)
        {
            case "FiscalDayClosed":
                state.Status = FiscalDayLifecycleStatus.Closed;
                state.ClosedAtUtc ??= nowUtc;
                state.LastError = null;
                Touch(state, nowUtc);
                result.Reconciled++;

                logger.LogInformation(
                    "Fiscal day {FiscalDayNo} on device {DeviceId} was closed after all; FDMS reports the day closed.",
                    state.FiscalDayNo,
                    state.DeviceId);
                break;

            case "FiscalDayOpened":
            case "FiscalDayCloseFailed":
                // FDMS saying the day is still open is proof the close did not take, which is the one reading
                // that makes another attempt safe.
                state.Status = FiscalDayLifecycleStatus.Drained;
                state.LastError = $"The close did not take effect; FDMS reports {status.FiscalDayStatus}.";
                Touch(state, nowUtc);
                result.Reconciled++;
                break;

            default:
                state.LastError = Truncate(
                    $"The close outcome is still unknown; FDMS reports {status.FiscalDayStatus}.", 2000);
                Touch(state, nowUtc);
                break;
        }
    }

    private async Task ResolveUploadFromFileListAsync(
        FiscalDayStateEntity state,
        DateTime nowUtc,
        DateTime nowLocal,
        FiscalDayLifecycleRunResult result,
        CancellationToken cancellationToken)
    {
        // Wide enough to cover a day uploaded late and a clock that disagrees by a few hours, narrow enough
        // that FDMS is not asked for a taxpayer's whole history.
        var from = (state.OpenedAtLocal ?? nowLocal.AddDays(-7)).AddDays(-1);
        var till = nowLocal.AddDays(1);

        GetFileStatusApiResponse files;

        try
        {
            files = await adminClient.GetOfflineFileStatusAsync(state.DeviceId, from, till, cancellationToken);
        }
        catch (FiscalisationApiException ex)
        {
            state.LastError = Truncate(
                $"The upload outcome is still unknown; FDMS's accepted-file list could not be read: {ex.Message}",
                2000);
            Touch(state, nowUtc);
            return;
        }

        var forThisDay = files.FileStatus
            .Where(file => file.FiscalDayNo == state.FiscalDayNo)
            .ToList();

        if (forThisDay.Count == 0)
        {
            state.LastError =
                "FDMS lists no accepted file for this fiscal day. The upload may never have landed, but "
                + "re-uploading it is not safe to do unattended — check the platform's Offline Files page.";
            Touch(state, nowUtc);
            return;
        }

        var rejected = forThisDay
            .Where(file => !string.IsNullOrWhiteSpace(file.FileProcessingErrorCode))
            .ToList();

        if (rejected.Count > 0)
        {
            // FDMS processed the file and said no. That is an answer, not an unknown — the day is settled as
            // failed and needs a person to rebuild it, not another pass.
            MarkFailed(
                state,
                "FDMS rejected this day's offline file: "
                + string.Join("; ", rejected.Select(file =>
                    $"sequence {file.FileSequence} — {file.FileProcessingErrorCode}")),
                nowUtc,
                result);
            return;
        }

        var closingFile = forThisDay.FirstOrDefault(file => file.HasFooter);

        if (closingFile is null)
        {
            state.LastError =
                $"FDMS holds {forThisDay.Count} file(s) for this day but none of them declares it closed, so the "
                + "day is still open at FDMS.";
            Touch(state, nowUtc);
            return;
        }

        if (!forThisDay.All(file =>
                string.Equals(file.FileProcessingStatus, FileProcessedSuccessfully, StringComparison.OrdinalIgnoreCase)))
        {
            state.LastError = "FDMS is still processing this day's file(s).";
            Touch(state, nowUtc);
            return;
        }

        state.Status = FiscalDayLifecycleStatus.Submitted;
        state.FileSubmittedAtUtc ??= closingFile.FileUploadDate ?? nowUtc;
        state.OfflineFileReference = Truncate(closingFile.OperationID, 100) ?? state.OfflineFileReference;
        state.LastError = null;
        Touch(state, nowUtc);

        result.Reconciled++;
        result.DaysSubmitted++;

        logger.LogInformation(
            "Fiscal day {FiscalDayNo} on device {DeviceId} did reach ZIMRA after all: FDMS reports every file for "
            + "the day processed, operation {OperationId}.",
            state.FiscalDayNo,
            state.DeviceId,
            closingFile.OperationID);
    }

    // ── Bookkeeping ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every reason this device's day must stay open, counted fresh.
    /// </summary>
    /// <remarks>
    /// Three questions rather than one, because a query keyed on (device, day) cannot see a receipt that
    /// is missing either column — and those rows exist by construction. A sale claims a receipt sequence
    /// on its global number alone, while carrying a signature needs a fiscal day too, so the ingest writes
    /// <see cref="DesktopSaleReceiptIngestStatus.Unsignable"/> rows with a null day, and a null device
    /// whenever the handset's device string is not a number. Each of those spent a number inside some day
    /// on some device, and the (device, day) query returns none of them.
    ///
    /// <list type="number">
    ///   <item><b>This day's arrears</b> — signed receipts the platform has not archived yet. They drain
    ///     on their own.</item>
    ///   <item><b>The device's chain holes</b> — anything Unsignable or ChainBroken on this device,
    ///     whatever day it names or fails to name. The platform will not accept the receipt that follows
    ///     one of these, so the chain is stopped there regardless of which day the row claims, and closing
    ///     a day over it strands everything behind it.</item>
    ///   <item><b>Receipts belonging to nobody</b> — a spent chain number with no device id. It cannot be
    ///     attributed, so it might be this device's, and it holds every device until a person says
    ///     otherwise.</item>
    /// </list>
    ///
    /// <see cref="DesktopSaleReceiptIngestStatus.Unstamped"/> is settled rather than owed in all three,
    /// and that is the whole reason it exists as its own status: it consumed no receipt number, so nothing
    /// in the chain is waiting behind it and the day can close over it — whereas treating it as owed would
    /// stop the day of every van still on a pre-signing build.
    /// </remarks>
    private async Task<OutstandingReceipts> CountOutstandingReceiptsAsync(
        FiscalDayStateEntity state,
        CancellationToken cancellationToken)
    {
        var fiscalDayNo = state.FiscalDayNo.ToString(CultureInfo.InvariantCulture);

        var forThisDay = await context.DesktopSales
            .AsNoTracking()
            .CountAsync(
                sale => sale.FiscalDeviceId == state.DeviceId
                        && sale.FiscalDayNo != null
                        // Trimmed, because the day number is a free-text column and " 7" is day 7.
                        && sale.FiscalDayNo.Trim() == fiscalDayNo
                        && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Ingested
                        && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Unstamped
                        && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.NotApplicable
                        // Counted by the device-wide question below instead, so they are not counted twice.
                        && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Unsignable
                        && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.ChainBroken,
                cancellationToken);

        var chainHoles = await context.DesktopSales
            .AsNoTracking()
            .CountAsync(
                sale => sale.FiscalDeviceId == state.DeviceId
                        && (sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unsignable
                            || sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.ChainBroken),
                cancellationToken);

        var unattributable = await context.DesktopSales
            .AsNoTracking()
            .CountAsync(
                sale => sale.FiscalDeviceId == null
                        && (sale.ReceiptGlobalNo > 0 || sale.ReceiptCounter > 0)
                        && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Ingested
                        && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Unstamped
                        && sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.NotApplicable,
                cancellationToken);

        return new OutstandingReceipts(forThisDay, chainHoles, unattributable);
    }

    private void MarkNeedsReconciliation(
        FiscalDayStateEntity state,
        string detail,
        DateTime nowUtc,
        FiscalDayLifecycleRunResult result)
    {
        var wasAlready = state.Status == FiscalDayLifecycleStatus.NeedsReconciliation;

        state.Status = FiscalDayLifecycleStatus.NeedsReconciliation;
        state.LastError = Truncate(detail, 2000);
        Touch(state, nowUtc);

        if (!wasAlready)
        {
            result.NeedsReconciliation++;
        }

        result.Errors.Add(
            $"Device {state.DeviceId}, fiscal day {state.FiscalDayNo}: outcome unknown — {detail}");

        logger.LogError(
            "Fiscal day {FiscalDayNo} on device {DeviceId} is in an unknown state and must not be retried: "
            + "{Detail}. It will be resolved by reading FDMS, not by repeating the operation.",
            state.FiscalDayNo,
            state.DeviceId,
            detail);
    }

    /// <summary>
    /// Records a refusal without moving the day, so the next pass resumes from the step it was on.
    /// </summary>
    /// <remarks>
    /// The status stays where it is deliberately. It names the furthest step reached, which is what
    /// "resume from the persisted status" means — writing a failure into it would lose the one fact the
    /// next run needs. <see cref="FiscalDayLifecycleStatus.Failed"/> is reserved for a day that has run out
    /// of attempts or been refused by FDMS itself.
    /// </remarks>
    private void RecordFailure(
        FiscalDayStateEntity state,
        string detail,
        DateTime nowUtc,
        FiscalDayLifecycleRunResult result)
    {
        state.LastError = Truncate(detail, 2000);
        Touch(state, nowUtc);

        result.Errors.Add($"Device {state.DeviceId}, fiscal day {state.FiscalDayNo}: {detail}");
    }

    /// <summary>Settles a day as refused for a stated reason. Nothing automatic touches it again.</summary>
    private void MarkFailed(
        FiscalDayStateEntity state,
        string detail,
        DateTime nowUtc,
        FiscalDayLifecycleRunResult result)
    {
        state.Status = FiscalDayLifecycleStatus.Failed;
        state.LastError = Truncate(detail, 2000);
        Touch(state, nowUtc);

        result.Failed++;
        result.Errors.Add($"Device {state.DeviceId}, fiscal day {state.FiscalDayNo}: {detail}");

        logger.LogError(
            "Fiscal day {FiscalDayNo} on device {DeviceId} was refused and will not be advanced again: {Detail}",
            state.FiscalDayNo,
            state.DeviceId,
            detail);
    }

    private static void Touch(FiscalDayStateEntity state, DateTime nowUtc) => state.UpdatedAt = nowUtc;

    /// <summary>Falls back to the documented default rather than refusing to run on a mistyped time.</summary>
    private static TimeOnly ParseCloseAtLocalTime(string? configured)
        => TimeOnly.TryParse(configured, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : new TimeOnly(20, 30);

    private static string FormatSequences(IEnumerable<int> sequences)
        => string.Join(",", sequences.Select(sequence => sequence.ToString(CultureInfo.InvariantCulture)));

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

    private sealed record FiscalDayReceiptSnapshot(
        int DeviceId,
        int FiscalDayNo,
        DateTime? OpenedAtLocal,
        int Ingested,
        int Outstanding);

    /// <summary>Everything holding one day open, kept apart because they are resolved by different people.</summary>
    private sealed record OutstandingReceipts(int ForThisDay, int ChainHoles, int Unattributable)
    {
        public int Total => ForThisDay + ChainHoles + Unattributable;
    }
}

/// <summary>What one pass of the lifecycle did, for the job to log and the console to show.</summary>
public sealed class FiscalDayLifecycleRunResult
{
    /// <summary>Fiscalisation is switched off, so nothing was looked at.</summary>
    public bool Skipped { get; set; }

    /// <summary>
    /// No platform service account is configured, so no fiscal-day route could be called. Reported rather
    /// than thrown: it is a deployment step that has not happened, not a fault.
    /// </summary>
    public bool ServiceAccountMissing { get; set; }

    /// <summary>
    /// Days this pass looked at, which is the unfinished ones rather than every day on record. A day
    /// already with ZIMRA, or closed at FDMS with nothing to package, is not counted because nothing about
    /// it can change.
    /// </summary>
    public int DaysTracked { get; set; }

    public int DaysAdvanced { get; set; }

    public int DaysClosed { get; set; }

    /// <summary>Days that reached ZIMRA on this pass.</summary>
    public int DaysSubmitted { get; set; }

    public int DaysBlockedByOutstandingReceipts { get; set; }

    /// <summary>Signed receipts for a blocked day that the platform has not archived yet.</summary>
    /// <remarks>These drain on their own once the platform is reachable.</remarks>
    public int OutstandingReceipts { get; set; }

    /// <summary>
    /// Receipts that took a chain number and can never be submitted as they stand, counted once per device.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="OutstandingReceipts"/> because nothing automatic clears one. The platform
    /// will not accept the receipt after it, so it holds every later day on that device too.
    /// </remarks>
    public int ChainHoleReceipts { get; set; }

    /// <summary>
    /// Receipts carrying a chain number but no device id, which hold every device's close.
    /// </summary>
    /// <remarks>
    /// One estate-wide figure rather than a per-day sum. They cannot be attributed, so any of them might
    /// belong to the day about to be closed.
    /// </remarks>
    public int UnattributableReceipts { get; set; }

    /// <summary>Days that used up their attempts and are waiting out the pause before another set.</summary>
    public int DaysAwaitingRetryBudget { get; set; }

    /// <summary>
    /// Devices with a day to advance and no credential that can act for them.
    /// </summary>
    /// <remarks>
    /// The platform authorises each call against the device named in it, so this is a fleet whose
    /// configuration has one account where it needs one per device.
    /// </remarks>
    public int DevicesWithoutServiceAccount { get; set; }

    public int DurationWarnings { get; set; }

    public int NeedsReconciliation { get; set; }

    /// <summary>Days settled on this pass by reading FDMS rather than by repeating anything.</summary>
    public int Reconciled { get; set; }

    public int Failed { get; set; }

    public List<string> Errors { get; } = [];
}
