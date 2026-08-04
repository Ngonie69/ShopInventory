namespace ShopInventory.Web.Services;

/// <summary>
/// One progress report from a master-data sync.
/// </summary>
/// <param name="Phase">What the sync is doing right now, ready to show as-is.</param>
/// <param name="Completed">Phases finished before <paramref name="Phase"/> started.</param>
/// <param name="Total">Phases in this sync.</param>
/// <remarks>
/// Progress is counted in phases, not estimated from elapsed time, so every
/// value a caller displays is a fact about the work rather than a guess at how
/// long the rest will take. The trade-off is that phases are not equal in
/// length — the API fetch usually dominates — so a bar driven by this advances
/// in jumps and will sit still for most of a slow sync.
/// </remarks>
public readonly record struct SyncProgress(string Phase, int Completed, int Total);

/// <summary>
/// Walks a sync through its phases and hands each one to the caller's
/// <see cref="IProgress{T}"/>. A null <paramref name="progress"/> makes every
/// call a no-op, which is what the background refresh paths use — they have
/// nobody to report to.
/// </summary>
internal sealed class SyncPhaseReporter(IProgress<SyncProgress>? progress, int totalPhases)
{
    private int _done;

    /// <summary>
    /// Announces the phase about to start, and counts the previous one as done.
    /// Call this immediately before the work it names, so the label always
    /// describes what is happening rather than what just happened.
    /// </summary>
    public void Next(string phase)
    {
        progress?.Report(new SyncProgress(phase, _done, totalPhases));
        _done++;
    }

    /// <summary>
    /// Reports the sync as finished. Needed because <see cref="Next"/> only ever
    /// reports the phases *before* the current one, so without this the last
    /// phase would never show as complete.
    /// </summary>
    public void Complete()
    {
        _done = totalPhases;
        progress?.Report(new SyncProgress("Finishing up", totalPhases, totalPhases));
    }
}
