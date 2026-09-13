using ShopInventory.Web.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The fiscalisation console's send gate, and the bulk run built on it.
/// </summary>
/// <remarks>
/// The console offers three ways to send the same document — the row's own button, its checkbox, and
/// the band's two page-wide buttons — and all three read <see cref="FiscalWorkQueueDisposition.CanSend"/>.
/// These tests exist because a fiscal receipt cannot be withdrawn: if a bulk path ever accepts a row the
/// single-row path refuses, the result is a duplicate at FDMS that nobody can take back.
/// </remarks>
public sealed class FiscalConsoleSendGateTests
{
    [Fact]
    public void A_retryable_row_with_a_docnum_may_be_sent()
    {
        Assert.True(FiscalWorkQueueDisposition.CanSend(Row(), NothingLockedOut));
    }

    [Theory]
    [InlineData(FiscalWorkQueueDisposition.Reconcile)]
    [InlineData(FiscalWorkQueueDisposition.Unrecoverable)]
    [InlineData(FiscalWorkQueueDisposition.Stalled)]
    [InlineData(FiscalWorkQueueDisposition.Automatic)]
    public void Every_other_disposition_is_refused(string disposition)
    {
        // Reconcile and Unrecoverable must never be sent; Stalled and Automatic belong to something
        // other than this page. A default of "retry" for an unrecognised value would be the wrong way
        // round, so the comparison is against Retry and nothing else.
        Assert.False(FiscalWorkQueueDisposition.CanSend(Row(disposition: disposition), NothingLockedOut));
    }

    [Fact]
    public void An_unrecognised_disposition_is_refused()
    {
        Assert.False(FiscalWorkQueueDisposition.CanSend(Row(disposition: "something-new"), NothingLockedOut));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_row_with_no_sap_docnum_is_refused(int? docNum)
    {
        // The fiscalise route is keyed on the SAP invoice. A sale that never reached SAP has nothing to
        // send, and sending DocNum 0 would ask the lookup for an invoice that cannot exist.
        Assert.False(FiscalWorkQueueDisposition.CanSend(Row(docNum: docNum), NothingLockedOut));
    }

    [Fact]
    public void A_row_locked_out_this_session_is_refused_even_though_the_queue_still_says_retry()
    {
        // The queue row is refetched on a schedule the operator does not control, so it will still read
        // "retry" after a send came back unresolved. This is the window that closes.
        var item = Row();

        Assert.False(FiscalWorkQueueDisposition.CanSend(item, new HashSet<string> { item.Key }));
    }

    [Fact]
    public void The_gate_that_hides_a_checkbox_is_the_gate_that_hides_the_button()
    {
        // The property under test is the page's, not this method's: select-all is built by filtering the
        // page through the same predicate the row's own action cell reads, so a bulk run cannot contain
        // a row whose button says "do not retry — reconcile".
        var page = new[]
        {
            Row(key: "a"),
            Row(key: "b", disposition: FiscalWorkQueueDisposition.Reconcile),
            Row(key: "c", docNum: null),
            Row(key: "d")
        };

        var lockedOut = new HashSet<string> { "d" };

        var sendable = page.Where(item => FiscalWorkQueueDisposition.CanSend(item, lockedOut)).ToList();

        Assert.Equal(["a"], sendable.Select(item => item.Key));
    }

    private static readonly IReadOnlySet<string> NothingLockedOut = new HashSet<string>();

    private static FiscalConsoleWorkItemResponse Row(
        string key = "invoice:772575",
        string disposition = FiscalWorkQueueDisposition.Retry,
        int? docNum = 772575) =>
        new()
        {
            Key = key,
            Disposition = disposition,
            DocNum = docNum,
            Reference = "772575",
            Source = "SAP invoice"
        };
}
