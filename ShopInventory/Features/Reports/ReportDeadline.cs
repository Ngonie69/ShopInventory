namespace ShopInventory.Features.Reports;

/// <summary>
/// The one budget every report runs against, and the linked source that enforces it.
/// </summary>
/// <remarks>
/// <para>
/// The budget has to stay clear of the two ceilings either side of it: the SAP client's
/// per-request timeout (<c>SAPSettings.RequestTimeoutMinutes</c>) and the Web app's own
/// <c>HttpClient.Timeout</c>. When all three were five minutes they raced, and a long run was as
/// likely to end as a caller-side abort as it was to reach the handler's timeout arm — so the one
/// message that tells the user what to do next never rendered. Keeping this budget the shortest of
/// the three makes the deadline ours, and the outcome the same every time.
/// </para>
/// <para>
/// It lives here rather than once per handler because fifteen copies of a number that only works
/// while it stays below two others is fifteen chances to raise one of them back into the race.
/// </para>
/// </remarks>
public static class ReportDeadline
{
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(4);

    /// <summary>
    /// A source that cancels when the caller gives up or the budget elapses, whichever comes first.
    /// </summary>
    /// <remarks>
    /// Linking is the part that is easy to leave out, and it does not announce itself when it is
    /// missing: an unlinked source still times out, still returns, still reads correct. What it
    /// stops doing is noticing that the caller has gone — so a report nobody is waiting for holds
    /// its SAP concurrency slot for the full budget, behind requests that do have someone waiting.
    /// </remarks>
    public static CancellationTokenSource Start(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Budget);
        return cts;
    }
}
