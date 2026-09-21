using System.Text.Json;

namespace ShopInventory.Services.Telematics;

/// <summary>
/// Where the day rollup has got to, stored as JSON in <c>SystemConfigs</c>.
/// </summary>
/// <remarks>
/// A stored checkpoint rather than a derived one because the backfill is rate-limited work spread
/// over several nights: the provider caps the events endpoint at 24 hours per call, so thirty days
/// of history is thirty paginated calls and cannot be done in one pass. Deriving "where did I get
/// to" from the rollup table would mean a day that legitimately has no data — a van off the road —
/// being retried for ever.
/// </remarks>
public sealed record CartrackRollupCheckpoint
{
    /// <summary>The oldest date the backfill is working towards. Set on the first run.</summary>
    public DateTime? BackfillStartDate { get; init; }

    /// <summary>The oldest date already built. The backfill walks forward from here.</summary>
    public DateTime? BackfillThroughDate { get; init; }

    public bool BackfillCompleted { get; init; }

    public DateTime? BackfillCompletedAtUtc { get; init; }

    /// <summary>The most recent date built by the incremental pass.</summary>
    public DateTime? LastBuiltDate { get; init; }

    /// <summary>
    /// When the reconciliation pass last re-read its window. Once a calendar day is enough: it
    /// exists because the provider reprocesses fuel accuracy and accepts late events, not because
    /// anything changes minute to minute.
    /// </summary>
    public DateTime? LastReconciledAtUtc { get; init; }
}

/// <summary>
/// Whether the rollup can speak for a given period, and why not when it cannot.
/// </summary>
/// <remarks>
/// <c>Reason</c> is a sentence to print verbatim, and is null when the rollup is ready. The
/// report shows it rather than inventing an explanation for an empty column, because "switched
/// off", "not backfilled that far" and "the sync is stale" are three different things to do
/// something about.
/// </remarks>
public sealed record CartrackRollupStatus(
    bool Enabled,
    bool Configured,
    bool Ready,
    DateTime? LastSyncedAtUtc,
    DateTime? CoveredFrom,
    DateTime? CoveredThrough,
    string? Reason);

/// <summary>
/// Reads a stored checkpoint. Shared, so the service that writes it and the one that reports on
/// it cannot drift about what an unreadable value means.
/// </summary>
public static class CartrackRollupCheckpointReader
{
    /// <summary>
    /// The stored checkpoint, an empty one when nothing is stored, or <b>null</b> when the stored
    /// value cannot be read — which the writer treats as "reset it" and the reader as "we do not
    /// know how far this has got".
    /// </summary>
    public static CartrackRollupCheckpoint? TryRead(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return new CartrackRollupCheckpoint();
        }

        try
        {
            return JsonSerializer.Deserialize<CartrackRollupCheckpoint>(stored)
                   ?? new CartrackRollupCheckpoint();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
