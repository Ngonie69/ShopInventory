using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The rule that stops a sale being invoiced twice when SAP's reply goes missing.
/// </summary>
/// <remarks>
/// <para><b>The bug this is here for.</b> Five sales were invoiced twice on KEFALOS_USD_NEW2 between
/// 25 August and 8 September 2026 — across tills and vans alike, each pair between nought and three
/// minutes apart, none cancelled, and one pair (GRC-FAC-20260907-8C34E7) closed and paid 3.42 on
/// both. About one sale in 1,300.</para>
///
/// <para><b>Why the old guard could not have caught them.</b> It was a read:
/// <c>GetInvoiceByVanSaleOrderAsync</c>, filtering on a UDF. It fails closed, so a lookup that
/// errors never causes a post. The only remaining window is the lookup <i>succeeding and returning
/// nothing</i> for an invoice SAP has committed but not yet made visible — and no amount of retrying
/// a read fixes that. The fix has to be a local record that a post went out, written before it does.</para>
///
/// <para>These pin the two halves that decide behaviour: whether a failure proves nothing was
/// created, and how long the lookup's "no" stays untrustworthy.</para>
/// </remarks>
public sealed class UnresolvedPostGraceTests
{
    // ---------------------------------------------------------------
    // Did SAP definitely not create the document?
    // ---------------------------------------------------------------

    [Fact]
    public void A_refusal_from_SAP_proves_nothing_was_created()
    {
        // SAP answered the post, and the answer was no.
        var rejected = new SapRequestRejectedException(
            "create the invoice", System.Net.HttpStatusCode.BadRequest, "item is blocked for sale");

        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(rejected));
    }

    [Fact]
    public void A_request_that_was_never_sent_proves_nothing_was_created()
    {
        // The client's own validation, before anything goes over the wire.
        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(new ArgumentException("Line 1: Quantity")));

        // SAP rejecting the posting dates is an answer too.
        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(
            new SapPostingPeriodException("posting period closed", "2026-09-08", "date not in period")));
    }

    [Fact]
    public void A_timeout_proves_nothing_either_way()
    {
        // The case that produced the duplicates. SAP may well hold the invoice.
        Assert.False(SapFailureClassifier.DefinitelyNotCommitted(
            new TimeoutException("The SAP Service Layer did not respond in time.")));

        Assert.False(SapFailureClassifier.DefinitelyNotCommitted(
            new HttpRequestException("The connection was closed unexpectedly.")));
    }

    [Fact]
    public void An_unreadable_reply_proves_nothing_either_way()
    {
        // CreateInvoiceAsync throws a bare Exception when it cannot deserialise SAP's answer — and
        // that happens *after* SAP has committed the document. Treating a bare Exception as proof of
        // non-commitment would reintroduce the bug through the one door it is hardest to see.
        Assert.False(SapFailureClassifier.DefinitelyNotCommitted(
            new Exception("Failed to deserialize created invoice")));
    }

    // ---------------------------------------------------------------
    // A refusal is not an outage
    // ---------------------------------------------------------------

    [Fact]
    public void A_refusal_is_never_treated_as_transient()
    {
        var rejected = new SapRequestRejectedException(
            "create the invoice", System.Net.HttpStatusCode.BadRequest, "item is blocked for sale");

        // Its own message reads "SAP refused to create the invoice", and this classifier looks for
        // the word "refused" to spot a *connection* refused. So the message says transient and the
        // truth is the opposite: the type has to win, or a rejected sale never spends an attempt and
        // retries until somebody notices.
        Assert.Contains("refused", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(SapFailureClassifier.ContainsAvailabilitySignal(rejected.Message));
        Assert.False(SapFailureClassifier.IsTransient(rejected));
    }

    [Fact]
    public void A_real_connection_refused_is_still_transient()
    {
        // The case the word was there for in the first place.
        Assert.True(SapFailureClassifier.IsTransient(
            new InvalidOperationException("No connection could be made because the target machine actively refused it")));
    }

    // ---------------------------------------------------------------
    // How long the lookup's "no" stays untrustworthy
    // ---------------------------------------------------------------

    [Fact]
    public void The_grace_window_outlasts_the_lag_that_caused_the_duplicates()
    {
        // The observed pairs were 0-3 minutes apart. The default has to be comfortably past that or
        // it closes nothing; every one of the five would have fallen inside 15 minutes.
        Assert.True(new Configuration.VanSalesPostingSettings().UnresolvedPostGraceMinutes >= 10);
        Assert.True(new Configuration.DesktopSalePostingSettings().UnresolvedPostGraceMinutes >= 10);
    }

    [Fact]
    public void The_window_can_be_switched_off_without_a_deploy()
    {
        // It delays a sale that genuinely never posted, so there has to be a way back to the old
        // behaviour during an incident.
        Assert.Equal(0, new Configuration.VanSalesPostingSettings { UnresolvedPostGraceMinutes = 0 }.UnresolvedPostGraceMinutes);
    }
}
