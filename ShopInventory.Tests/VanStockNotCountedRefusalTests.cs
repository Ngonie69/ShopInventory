using System.Text.RegularExpressions;
using ShopInventory.Common.Stock;

namespace ShopInventory.Tests;

/// <summary>
/// What a rep reads when a van's sale cannot be checked has to be true, and has to reach the screen as
/// written.
/// </summary>
/// <remarks>
/// <para>On 2026-10-11 VAN004 was refused three times before the 07:00 stock read with "SAP is down, and
/// van VAN004 has not sent today's stock count … Open Start the day, or sync the handset, to send the
/// count". SAP was not down, it had left one stock read unanswered; and the handset's count has been
/// written nowhere since 2026-09-29, so nothing the rep could do would have changed the answer.</para>
///
/// <para>The handset is a separate repository (DBSDEVS/KefalosVanSales) and decides what to show by
/// searching the server's text for keywords. <see cref="HandsetSaleErrorReading"/> is that code, copied,
/// so a wording that trips a keyword fails here instead of on a rep's screen.</para>
/// </remarks>
public sealed class VanStockNotCountedRefusalTests
{
    private static readonly TimeSpan SevenCat = new(7, 0, 0);

    /// <summary>
    /// The first of the three refusals, 04:24:30 by the log. Taken as UTC it is 06:24 CAT; taken as
    /// CAT it is earlier still. After midnight and before the read either way.
    /// </summary>
    private static readonly DateTime BeforeTheRead = new(2026, 10, 11, 4, 24, 30, DateTimeKind.Utc);

    /// <summary>08:30 CAT the same day: the read was due an hour and a half ago.</summary>
    private static readonly DateTime AfterTheRead = new(2026, 10, 11, 6, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Before_the_morning_read_a_stock_read_sap_left_unanswered_says_so()
    {
        var (message, action) = VanStockNotCountedRefusal.Describe(
            "VAN004", sapHeldBack: false, BeforeTheRead, SevenCat);

        Assert.Equal(
            "SAP did not answer when asked for the van's stock, and this system has no figure of its own "
            + "for van VAN004 before the 07:00 stock read, so the sale cannot be checked. Nothing on the "
            + "handset changes this. Try the sale again in a few minutes, and tell the office if it is "
            + "still refused.",
            message);
        Assert.Equal("Try the sale again in a few minutes, and tell the office if it is still refused", action);
    }

    [Fact]
    public void With_sap_held_back_it_does_not_claim_sap_was_asked()
    {
        var (message, _) = VanStockNotCountedRefusal.Describe(
            "VAN004", sapHeldBack: true, BeforeTheRead, SevenCat);

        Assert.StartsWith("SAP is not available right now, and ", message);
        Assert.DoesNotContain("did not answer", message);
    }

    /// <summary>
    /// After seven the read was due. A van without one then is a read that is still running, that
    /// failed, or that never started, and the office can run it; "before the 07:00 stock read" would
    /// send the rep to wait for something that has already been and gone.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Once_the_morning_read_was_due_it_says_the_read_has_not_finished(bool sapHeldBack)
    {
        var (message, action) = VanStockNotCountedRefusal.Describe("VAN004", sapHeldBack, AfterTheRead, SevenCat);

        Assert.Contains("this morning's 07:00 stock read has not finished for van VAN004", message);
        Assert.DoesNotContain("before the", message);
        Assert.Contains("tell the office so the stock read can be run again for the van", action);
    }

    /// <summary>
    /// Half past midnight CAT is still the evening before in UTC. The gap runs from the CAT midnight,
    /// which is where the day's snapshot date rolls for a van.
    /// </summary>
    [Fact]
    public void Just_after_midnight_cat_is_before_the_read()
    {
        var halfPastMidnightCat = new DateTime(2026, 10, 10, 22, 30, 0, DateTimeKind.Utc);

        var (message, _) = VanStockNotCountedRefusal.Describe(
            "VAN004", sapHeldBack: false, halfPastMidnightCat, SevenCat);

        Assert.Contains("before the 07:00 stock read", message);
    }

    [Fact]
    public void The_read_time_is_the_configured_one()
    {
        var (message, _) = VanStockNotCountedRefusal.Describe(
            "VAN004", sapHeldBack: false, BeforeTheRead, new TimeSpan(6, 30, 0));

        Assert.Contains("before the 06:30 stock read", message);
    }

    public static TheoryData<bool, DateTime> EveryWording => new()
    {
        { false, BeforeTheRead },
        { true, BeforeTheRead },
        { false, AfterTheRead },
        { true, AfterTheRead }
    };

    /// <summary>
    /// The count is not something a rep can send: <c>ReportVanSalesStockPositionHandler</c> answers
    /// <c>accepted</c> and writes nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryWording))]
    public void No_wording_tells_the_rep_to_send_a_count(bool sapHeldBack, DateTime utcNow)
    {
        var (message, action) = VanStockNotCountedRefusal.Describe("VAN004", sapHeldBack, utcNow, SevenCat);

        foreach (var text in new[] { message, action })
        {
            Assert.DoesNotContain("Start the day", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sync", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("count", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SAP is down", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// As the direct van sale and the order conversion answer it: the summary, then the reasons, joined
    /// with "; ".
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryWording))]
    public void Every_wording_reaches_the_handset_screen_as_written(bool sapHeldBack, DateTime utcNow)
    {
        var (message, _) = VanStockNotCountedRefusal.Describe("VAN004", sapHeldBack, utcNow, SevenCat);
        var answered = $"{VanStockNotCountedRefusal.Summary}; {message}";

        Assert.Equal($"Error: {answered}", HandsetSaleErrorReading.ParseSapError(answered));
        Assert.False(HandsetSaleErrorReading.IsDuplicateVanOrderError(answered));
        Assert.False(HandsetSaleErrorReading.OffersToEditTheBasket(answered));
    }

    /// <summary>
    /// The probe has to be able to fail. These are the readings the wording is kept clear of, each
    /// reached by a sentence that would have been a natural way to say the same thing.
    /// </summary>
    [Theory]
    [InlineData("SAP timed out reading van VAN004's stock.", "Request Timeout")]
    [InlineData("The connection closed before SAP answered for van VAN004.", "may or may not have been submitted")]
    [InlineData("SAP's reply for van VAN004 was aborted.", "may or may not have been submitted")]
    public void The_probe_catches_a_wording_that_trips_a_handset_keyword(string wording, string shownInstead)
    {
        Assert.Contains(shownInstead, HandsetSaleErrorReading.ParseSapError(wording));
    }

    [Fact]
    public void The_old_summary_offered_to_edit_the_basket()
    {
        Assert.True(HandsetSaleErrorReading.OffersToEditTheBasket("Insufficient stock available for reservation"));
    }
}

/// <summary>
/// The van handset's reading of a refused sale, copied from DBSDEVS/KefalosVanSales at
/// <c>origin/master</c> bf31c7e (2026-10-11): <c>Services/StockValidationHelper.cs</c> and the three
/// checks in <c>Views/SalesCartSummary.xaml.cs</c>.
/// </summary>
/// <remarks>
/// Copied rather than described, because the handset decides by substring and a paraphrase of a
/// substring list proves nothing. The basket lookups of the original are left out: they only reword
/// the "negative inventory" branch, which is reached or not on the same test.
/// </remarks>
internal static class HandsetSaleErrorReading
{
    /// <summary><c>StockValidationHelper.ParseSapError</c>, without the basket.</summary>
    public static string ParseSapError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return "An unknown error occurred";

        if (errorMessage.Contains("negative inventory", StringComparison.OrdinalIgnoreCase))
        {
            return "❌ Insufficient Stock\n\n" +
                   "One or more items in your order don't have enough stock in the system.\n\n" +
                   "Please verify quantities and try again.";
        }

        if (errorMessage.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return "⚠️ Duplicate Order\n\nThis order may have already been submitted.";
        }

        if (errorMessage.Contains("customer", StringComparison.OrdinalIgnoreCase) &&
            errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "⚠️ Customer Not Found\n\nThe selected customer was not found in SAP.";
        }

        if (errorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            errorMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "⏱️ Request Timeout\n\nThe server took too long to respond. Please try again.";
        }

        if (IsConnectionError(errorMessage))
        {
            return "🔌 Connection Error\n\n" +
                   "The connection to the server was interrupted.\n\n" +
                   "Your order may or may not have been submitted.\n" +
                   "Please check Sales History before resubmitting.";
        }

        return $"Error: {CleanErrorMessage(errorMessage)}";
    }

    /// <summary>
    /// <c>StockValidationHelper.IsConnectionError</c>. The sale screen's own copy of the list, which it
    /// runs over the exception first, is this one less "unable to connect".
    /// </summary>
    public static bool IsConnectionError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;

        var message = errorMessage.ToLowerInvariant();
        var connectionErrorPatterns = new[]
        {
            "socket",
            "connection closed",
            "connection reset",
            "connection refused",
            "connection timed out",
            "network is unreachable",
            "no route to host",
            "host not found",
            "name resolution",
            "ssl",
            "tls",
            "handshake",
            "aborted",
            "forcibly closed",
            "remote host",
            "underlying connection",
            "unable to connect"
        };

        return connectionErrorPatterns.Any(pattern => message.Contains(pattern));
    }

    /// <summary><c>SalesCartSummary.IsDuplicateVanOrderError</c>: sends the rep to Invoice History.</summary>
    public static bool IsDuplicateVanOrderError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;

        return error.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || error.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || error.Contains("van_order", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sale screen's <c>isStockIssue</c>: titles the alert "Insufficient Stock" and asks whether to
    /// edit the cart to adjust quantities.
    /// </summary>
    public static bool OffersToEditTheBasket(string? error)
    {
        var friendlyError = ParseSapError(error);

        return friendlyError.Contains("insufficient", StringComparison.OrdinalIgnoreCase) ||
            friendlyError.Contains("negative inventory", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanErrorMessage(string message)
    {
        var cleaned = Regex.Replace(message, @"Error The remote server returned an error:\s*\(\d+\)\s*", "");
        cleaned = Regex.Replace(cleaned, @"\[DocumentLines\.ItemCode\]", "");
        cleaned = Regex.Replace(cleaned, @"\[line:\s*\d+\]", "");
        cleaned = cleaned.Replace("\r", "").Replace("  ", " ").Trim();

        return cleaned;
    }
}
