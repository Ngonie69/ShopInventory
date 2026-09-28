using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

public interface ICreditNoteProjectionSyncService
{
    Task SyncAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the projection may be read in place of SAP: the job is on, the initial backfill has
    /// finished, and the last sync is inside the staleness window.
    /// </summary>
    Task<bool> IsReadyForReadsAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(
        IReadOnlyCollection<SAPCreditNote> creditNotes,
        CancellationToken cancellationToken = default);

    Task RefreshDocumentAsync(int sapDocEntry, CancellationToken cancellationToken = default);
}

/// <summary>
/// Maintains a line-level PostgreSQL projection of SAP A/R credit memos. Reads overlap by one day
/// and upserts are idempotent, so a retry or Quartz misfire cannot duplicate lines.
/// </summary>
/// <remarks>
/// The sweep runs every two minutes and SAP's UpdateDate has no time, so its window always spans
/// yesterday and today. It used to read every credit note in that window whole, with lines, and
/// rewrite every one of their rows, about 720 times a day for some thirty new documents. It now
/// polls only each document's version (UpdateDate, UpdateTime, status, cancellation and total),
/// fetches whole only the documents whose version moved, and writes only rows that changed.
/// </remarks>
public sealed class CreditNoteProjectionSyncService(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IOptions<CreditNoteSyncSettings> options,
    ILogger<CreditNoteProjectionSyncService> logger
) : ICreditNoteProjectionSyncService
{
    public const string CacheKey = "CreditNotes";
    private const string CheckpointConfigKey = "CreditNoteSync.Checkpoint";
    private const string DisplayName = "Credit Notes";
    private const int MaxErrorLength = 1000;
    private const int CommentsMaxLength = 254;
    private readonly CreditNoteSyncSettings _settings = options.Value;

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var syncState = await GetOrCreateSyncStateAsync(cancellationToken);
        syncState.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            var (checkpointRow, checkpoint) = await GetCheckpointAsync(now, cancellationToken);

            if (!checkpoint.BackfillCompleted)
            {
                checkpoint = await RunBackfillAsync(
                    checkpointRow,
                    checkpoint,
                    now,
                    cancellationToken);
            }

            (checkpoint, var inserted) = await RunIncrementalAsync(
                checkpointRow,
                checkpoint,
                now,
                cancellationToken);

            var reconciled = false;
            if (!checkpoint.LastReconciledAtUtc.HasValue ||
                checkpoint.LastReconciledAtUtc.Value.Date < now.Date)
            {
                var reconciliationFrom = now.Date.AddDays(-Math.Max(1, _settings.ReconciliationWindowDays));
                var recentCreditNotes = await sapClient.GetCreditNotesByDateRangeAsync(
                    reconciliationFrom,
                    now.Date,
                    cancellationToken);
                await UpsertCoreAsync(recentCreditNotes, polledUpdateTimes: null, cancellationToken);
                reconciled = true;

                checkpoint = checkpoint with { LastReconciledAtUtc = now };
                await SaveCheckpointAsync(checkpointRow, checkpoint, cancellationToken);
            }

            // Counted again only when this pass can have grown the table. A document added between
            // sweeps by RefreshDocumentAsync is counted by the next daily reconciliation.
            var previousItemCount = syncState.ItemCount;
            if (inserted > 0 || reconciled || syncState.ItemCount == 0)
            {
                syncState.ItemCount = await context.SapCreditNoteSnapshots.CountAsync(cancellationToken);
            }
            syncState.LastSyncedAt = now;
            syncState.LastError = null;
            syncState.LastErrorAt = null;
            syncState.UpdatedAt = now;
            await context.SaveChangesAsync(cancellationToken);

            // Only say so when something moved. The sweep runs every two minutes whether or not SAP
            // has anything new, and on 2026-08-20 that was 273 runs reporting the same header count
            // 225 times over — thirty new credit notes in a whole day, announced 273 times. The
            // count is still written to the sync state either way, so the "when did this last run"
            // question is answered from there rather than from the log.
            if (syncState.ItemCount != previousItemCount)
            {
                logger.LogInformation(
                    "Credit-note projection synchronized with {CreditNoteCount} header(s), {Delta:+#;-#;0} since the last sweep",
                    syncState.ItemCount,
                    syncState.ItemCount - previousItemCount);
            }
            else
            {
                logger.LogDebug(
                    "Credit-note projection synchronized successfully with {CreditNoteCount} header(s)",
                    syncState.ItemCount);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Credit-note projection synchronization failed");
            syncState.LastError = Truncate(ex.Message, MaxErrorLength);
            syncState.LastErrorAt = DateTime.UtcNow;
            syncState.UpdatedAt = DateTime.UtcNow;

            try
            {
                await context.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception stateException)
            {
                logger.LogWarning(stateException, "Failed to record credit-note synchronization failure");
            }

            throw;
        }
    }

    /// <summary>
    /// The backfill check is the important half: a projection can be perfectly fresh and still be
    /// only part-way through its first walk of SAP history, and reading it then would answer a
    /// query about an older month with silence rather than with rows.
    /// </summary>
    public async Task<bool> IsReadyForReadsAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            return false;
        }

        var syncState = await context.CacheSyncStates
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.CacheKey == CacheKey, cancellationToken);

        if (!CreditNoteProjectionFreshness.IsFresh(syncState, _settings, DateTime.UtcNow))
        {
            return false;
        }

        var checkpointRow = await context.SystemConfigs
            .AsNoTracking()
            .SingleOrDefaultAsync(config => config.Key == CheckpointConfigKey, cancellationToken);

        return TryReadCheckpoint(checkpointRow?.Value)?.BackfillCompleted == true;
    }

    public async Task UpsertAsync(
        IReadOnlyCollection<SAPCreditNote> creditNotes,
        CancellationToken cancellationToken = default) =>
        await UpsertCoreAsync(creditNotes, polledUpdateTimes: null, cancellationToken);

    /// <summary>
    /// Writes each document that differs from its row, and only those; returns how many rows it
    /// added. A document read back unchanged leaves its row, lines and timestamps untouched.
    /// </summary>
    /// <param name="creditNotes">Whole documents, as any credit-note read returns them.</param>
    /// <param name="polledUpdateTimes">
    /// The UpdateTime the version poll reported for each document. Whole reads do not carry one, so
    /// this is the only way a row's <see cref="SapCreditNoteSnapshotEntity.SapUpdateTime"/> is set,
    /// and a whole read without it leaves the stored time alone.
    /// </param>
    /// <param name="cancellationToken">Cancels the read and the write.</param>
    private async Task<int> UpsertCoreAsync(
        IReadOnlyCollection<SAPCreditNote> creditNotes,
        IReadOnlyDictionary<int, TimeOnly>? polledUpdateTimes,
        CancellationToken cancellationToken)
    {
        if (creditNotes.Count == 0)
        {
            return 0;
        }

        var normalizedCreditNotes = creditNotes
            .Where(note => note.DocEntry > 0)
            .GroupBy(note => note.DocEntry)
            .Select(group => group.First())
            .ToList();

        if (normalizedCreditNotes.Count == 0)
        {
            return 0;
        }

        var docEntries = normalizedCreditNotes.Select(note => note.DocEntry).ToList();
        var existing = await context.SapCreditNoteSnapshots
            .AsTracking()
            .Include(snapshot => snapshot.Lines)
            .Where(snapshot => docEntries.Contains(snapshot.SapDocEntry))
            .ToDictionaryAsync(snapshot => snapshot.SapDocEntry, cancellationToken);
        var syncedAt = DateTime.UtcNow;
        var inserted = 0;

        foreach (var source in normalizedCreditNotes)
        {
            var isNew = !existing.TryGetValue(source.DocEntry, out var snapshot);
            if (snapshot is null)
            {
                snapshot = new SapCreditNoteSnapshotEntity
                {
                    SapDocEntry = source.DocEntry
                };
                context.SapCreditNoteSnapshots.Add(snapshot);
                existing.Add(source.DocEntry, snapshot);
                inserted++;
            }

            MapHeader(snapshot, source, syncedAt);
            if (polledUpdateTimes?.TryGetValue(source.DocEntry, out var updateTime) == true)
            {
                snapshot.SapUpdateTime = updateTime;
            }

            var linesRemoved = UpsertLines(snapshot, source.DocumentLines ?? []);

            if (isNew || linesRemoved || HasPendingChanges(snapshot))
            {
                snapshot.LastSeenInSapAtUtc = syncedAt;
                snapshot.SyncedAtUtc = syncedAt;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        return inserted;
    }

    /// <summary>
    /// Asks the change tracker about this document's own entries only: the daily reconciliation
    /// holds thousands, and a whole-context scan per document would grow with the square of that.
    /// </summary>
    private bool HasPendingChanges(SapCreditNoteSnapshotEntity snapshot) =>
        IsPending(context.Entry(snapshot)) ||
        snapshot.Lines.Any(line => IsPending(context.Entry(line)));

    private static bool IsPending(EntityEntry entry)
    {
        entry.DetectChanges();
        return entry.State != EntityState.Unchanged;
    }

    public async Task RefreshDocumentAsync(
        int sapDocEntry,
        CancellationToken cancellationToken = default)
    {
        if (sapDocEntry <= 0)
        {
            return;
        }

        var creditNote = await sapClient.GetCreditNoteByDocEntryAsync(sapDocEntry, cancellationToken);
        if (creditNote is not null)
        {
            await UpsertAsync([creditNote], cancellationToken);
        }
    }

    private async Task<ProjectionCheckpoint> RunBackfillAsync(
        SystemConfigEntity checkpointRow,
        ProjectionCheckpoint checkpoint,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var backfillStartDate = checkpoint.BackfillStartDate;
        if (!backfillStartDate.HasValue)
        {
            backfillStartDate = await sapClient.GetEarliestCreditNoteDateAsync(cancellationToken)
                ?? now.Date;
            checkpoint = checkpoint with { BackfillStartDate = backfillStartDate.Value.Date };
            await SaveCheckpointAsync(checkpointRow, checkpoint, cancellationToken);
        }

        var firstDate = checkpoint.BackfillThroughDate?.Date.AddDays(1)
            ?? backfillStartDate.Value.Date;

        while (firstDate <= now.Date)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var monthEnd = new DateTime(
                firstDate.Year,
                firstDate.Month,
                DateTime.DaysInMonth(firstDate.Year, firstDate.Month),
                0,
                0,
                0,
                DateTimeKind.Utc);
            var throughDate = monthEnd < now.Date ? monthEnd : now.Date;

            var creditNotes = await sapClient.GetCreditNotesByDateRangeAsync(
                firstDate,
                throughDate,
                cancellationToken);
            await UpsertAsync(creditNotes, cancellationToken);

            checkpoint = checkpoint with { BackfillThroughDate = throughDate };
            await SaveCheckpointAsync(checkpointRow, checkpoint, cancellationToken);

            logger.LogInformation(
                "Credit-note projection backfilled {FromDate:yyyy-MM-dd} through {ToDate:yyyy-MM-dd} ({Count} document(s))",
                firstDate,
                throughDate,
                creditNotes.Count);

            firstDate = throughDate.AddDays(1);
        }

        checkpoint = checkpoint with
        {
            BackfillCompleted = true,
            BackfillCompletedAtUtc = now,
            LastUpdateWatermarkDate = now.Date
        };
        await SaveCheckpointAsync(checkpointRow, checkpoint, cancellationToken);
        return checkpoint;
    }

    private async Task<(ProjectionCheckpoint Checkpoint, int Inserted)> RunIncrementalAsync(
        SystemConfigEntity checkpointRow,
        ProjectionCheckpoint checkpoint,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var updateFrom = (checkpoint.LastUpdateWatermarkDate ?? now.Date.AddDays(-1))
            .Date
            .AddDays(-1);
        var versions = await sapClient.GetCreditNoteVersionsUpdatedSinceAsync(
            updateFrom,
            now.Date,
            cancellationToken);

        var inserted = 0;
        var moved = await FindMovedAsync(versions, cancellationToken);
        if (moved.Count > 0)
        {
            // The time is the one polled, not one read with the document. An edit landing between
            // the poll and the read is then stored under the older time, and the next poll fetches
            // the document again; the other way round, it would be stored as already seen.
            var polledUpdateTimes = versions
                .Where(version => moved.Contains(version.DocEntry))
                .Select(version => (version.DocEntry, Time: ParseSapTime(version.UpdateTime)))
                .Where(version => version.Time.HasValue)
                .GroupBy(version => version.DocEntry)
                .ToDictionary(group => group.Key, group => group.First().Time!.Value);
            var creditNotes = await sapClient.GetCreditNotesByDocEntriesAsync(moved, cancellationToken);
            inserted = await UpsertCoreAsync(creditNotes, polledUpdateTimes, cancellationToken);
        }

        checkpoint = checkpoint with { LastUpdateWatermarkDate = now.Date };
        await SaveCheckpointAsync(checkpointRow, checkpoint, cancellationToken);
        return (checkpoint, inserted);
    }

    /// <summary>
    /// The polled documents that are new to the projection or whose version differs from their
    /// row. A version without an UpdateTime cannot tell two edits on one day apart, so it always
    /// counts as moved, as does a row written before the projection kept UpdateTime.
    /// </summary>
    private async Task<List<int>> FindMovedAsync(
        IReadOnlyCollection<SAPCreditNote> versions,
        CancellationToken cancellationToken)
    {
        var polled = versions
            .Where(version => version.DocEntry > 0)
            .GroupBy(version => version.DocEntry)
            .Select(group => group.First())
            .ToList();
        if (polled.Count == 0)
        {
            return [];
        }

        var docEntries = polled.Select(version => version.DocEntry).ToList();
        var stored = await context.SapCreditNoteSnapshots
            .AsNoTracking()
            .Where(snapshot => docEntries.Contains(snapshot.SapDocEntry))
            .Select(snapshot => new
            {
                snapshot.SapDocEntry,
                snapshot.SapUpdateDate,
                snapshot.SapUpdateTime,
                snapshot.DocumentStatus,
                snapshot.IsCancelled,
                snapshot.DocTotal
            })
            .ToDictionaryAsync(snapshot => snapshot.SapDocEntry, cancellationToken);

        return polled
            .Where(version =>
            {
                if (!stored.TryGetValue(version.DocEntry, out var row))
                {
                    return true;
                }

                var updateTime = ParseSapTime(version.UpdateTime);
                return updateTime is null ||
                    row.SapUpdateTime != updateTime ||
                    row.SapUpdateDate != ParseSapDate(version.UpdateDate) ||
                    row.DocumentStatus != version.DocumentStatus?.Trim() ||
                    row.IsCancelled != IsCancelled(version.Cancelled) ||
                    row.DocTotal != Math.Round(version.DocTotal, 2, MidpointRounding.AwayFromZero);
            })
            .Select(version => version.DocEntry)
            .ToList();
    }

    private async Task<(SystemConfigEntity Row, ProjectionCheckpoint Checkpoint)> GetCheckpointAsync(
        DateTime now,
        CancellationToken cancellationToken)
    {
        var row = await context.SystemConfigs
            .AsTracking()
            .SingleOrDefaultAsync(config => config.Key == CheckpointConfigKey, cancellationToken);

        if (row is null)
        {
            row = new SystemConfigEntity
            {
                Key = CheckpointConfigKey,
                ValueType = "json",
                Category = "Synchronization",
                Description = "Resumable SAP credit-note projection checkpoint.",
                IsEditable = false,
                UpdatedAt = now
            };
            context.SystemConfigs.Add(row);
            await context.SaveChangesAsync(cancellationToken);
        }

        var checkpoint = TryReadCheckpoint(row.Value);
        if (checkpoint is null)
        {
            logger.LogWarning("Resetting invalid credit-note projection checkpoint");
            checkpoint = new ProjectionCheckpoint();
        }

        return (row, checkpoint);
    }

    /// <summary>
    /// Returns the stored checkpoint, an empty one when nothing is stored, or null when the stored
    /// value is not readable.
    /// </summary>
    private static ProjectionCheckpoint? TryReadCheckpoint(string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return new ProjectionCheckpoint();
        }

        try
        {
            return JsonSerializer.Deserialize<ProjectionCheckpoint>(storedValue) ?? new ProjectionCheckpoint();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SaveCheckpointAsync(
        SystemConfigEntity row,
        ProjectionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var value = JsonSerializer.Serialize(checkpoint);
        if (value == row.Value)
        {
            return;
        }

        row.Value = value;
        row.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<CacheSyncStateEntity> GetOrCreateSyncStateAsync(
        CancellationToken cancellationToken)
    {
        var state = await context.CacheSyncStates
            .AsTracking()
            .SingleOrDefaultAsync(entry => entry.CacheKey == CacheKey, cancellationToken);

        if (state is not null)
        {
            return state;
        }

        state = new CacheSyncStateEntity
        {
            CacheKey = CacheKey,
            DisplayName = DisplayName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        context.CacheSyncStates.Add(state);
        return state;
    }

    private static void MapHeader(
        SapCreditNoteSnapshotEntity target,
        SAPCreditNote source,
        DateTime syncedAt)
    {
        target.SapDocNum = source.DocNum;
        target.DocDate = ParseSapDate(source.DocDate) ?? syncedAt.Date;
        target.CardCode = source.CardCode?.Trim();
        target.CardName = source.CardName?.Trim();
        target.DocCurrency = source.DocCurrency?.Trim();
        target.Comments = Truncate(source.Comments?.Trim(), CommentsMaxLength);
        target.DocTotal = source.DocTotal;
        target.VatSum = source.VatSum;
        target.DocumentStatus = source.DocumentStatus?.Trim();
        target.IsCancelled = IsCancelled(source.Cancelled);
        target.SapUpdateDate = ParseSapDate(source.UpdateDate);
    }

    /// <summary>Returns whether any stored line was dropped because SAP no longer returns it.</summary>
    private static bool UpsertLines(
        SapCreditNoteSnapshotEntity snapshot,
        IReadOnlyCollection<SAPCreditNoteLine> sourceLines)
    {
        var existingLines = snapshot.Lines.ToDictionary(line => line.LineNum);
        var incomingLineNumbers = new HashSet<int>();

        foreach (var source in sourceLines)
        {
            incomingLineNumbers.Add(source.LineNum);
            if (!existingLines.TryGetValue(source.LineNum, out var target))
            {
                target = new SapCreditNoteLineSnapshotEntity
                {
                    CreditNoteDocEntry = snapshot.SapDocEntry,
                    LineNum = source.LineNum
                };
                snapshot.Lines.Add(target);
            }

            target.ItemCode = source.ItemCode?.Trim();
            target.BaseEntry = source.BaseEntry;
            target.BaseLine = source.BaseLine;
            target.BaseType = source.BaseType;
            target.LineTotal = source.LineTotal;
            target.VatSum = source.VatSum;
            target.CreditReason = source.CreditReason?.Trim();
        }

        var staleLines = snapshot.Lines
            .Where(line => !incomingLineNumbers.Contains(line.LineNum))
            .ToList();
        foreach (var staleLine in staleLines)
        {
            snapshot.Lines.Remove(staleLine);
        }

        return staleLines.Count > 0;
    }

    private static DateTime? ParseSapDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            return null;
        }

        return DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
    }

    private static TimeOnly? ParseSapTime(string? value) =>
        TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : null;

    private static bool IsCancelled(string? value) =>
        string.Equals(value, "tYES", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Y", StringComparison.OrdinalIgnoreCase);

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    private sealed record ProjectionCheckpoint(
        DateTime? BackfillStartDate = null,
        DateTime? BackfillThroughDate = null,
        bool BackfillCompleted = false,
        DateTime? BackfillCompletedAtUtc = null,
        DateTime? LastUpdateWatermarkDate = null,
        DateTime? LastReconciledAtUtc = null);
}
