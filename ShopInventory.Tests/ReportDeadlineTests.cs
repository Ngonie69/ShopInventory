using ShopInventory.Features.Reports;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the budget every report runs against, and the linking that thirteen handlers were missing.
/// </summary>
/// <remarks>
/// The missing link did not announce itself. An unlinked source still times out, still returns, and
/// still reads correct at the call site — what it stops doing is noticing that the caller has gone.
/// A report nobody was waiting for therefore held its SAP concurrency slot for the full budget,
/// ahead of requests that did have someone waiting, which is a shape worth a test rather than a
/// convention.
/// </remarks>
public sealed class ReportDeadlineTests
{
    /// <summary>
    /// The SAP client's per-request timeout and the Web app's HttpClient timeout, which the report
    /// budget has to stay under. Both are five minutes; see <see cref="ReportDeadline"/>.
    /// </summary>
    private static readonly TimeSpan SurroundingCeiling = TimeSpan.FromMinutes(5);

    [Fact]
    public void The_budget_stays_under_the_ceilings_either_side_of_it()
    {
        // Raising this to five would put the report back in a three-way race it loses at random,
        // which is what stopped the timeout message ever reaching the page.
        Assert.True(
            ReportDeadline.Budget < SurroundingCeiling,
            $"The report budget ({ReportDeadline.Budget}) must stay below the SAP and Web client "
            + $"timeouts ({SurroundingCeiling}) so the deadline is always the report's own.");
    }

    [Fact]
    public void A_caller_that_gives_up_cancels_the_report_with_it()
    {
        using var caller = new CancellationTokenSource();
        using var deadline = ReportDeadline.Start(caller.Token);

        Assert.False(deadline.IsCancellationRequested);

        caller.Cancel();

        Assert.True(
            deadline.IsCancellationRequested,
            "The deadline must be linked to the caller, or an abandoned report keeps its SAP slot.");
    }
}
