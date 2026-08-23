using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
// Both projects define a SalesOrderService and a SalesOrderStatus. The Web ones are what this
// helper is written against, so they are the plain names; the API ones are aliased and appear only
// where the two are deliberately compared.
using ApiSalesOrderService = ShopInventory.Services.SalesOrderService;
using ApiStatus = ShopInventory.Models.Entities.SalesOrderStatus;

namespace ShopInventory.Tests;

/// <summary>
/// Pins <see cref="SalesOrderActions"/>, the gates the two sales-order lists share.
/// </summary>
/// <remarks>
/// <para>
/// The gates used to be private copies on SalesOrders.razor and MobileDrafts.razor, and they
/// drifted: approval was offered on Draft by one page and not the other, which stranded every Web
/// order whose auto-post had been refused and reset it to Draft. The pages now call one helper, so
/// they cannot disagree with each other — these tests are what stops the helper disagreeing with
/// the API, which is the only remaining way the same bug comes back.
/// </para>
/// <para>
/// ShopInventory.Web does not reference the API project, so the helper is a hand-mirrored copy.
/// Every status is listed explicitly below rather than derived, so changing a gate means changing
/// this file and stating the new rule outright. Three checks run against live API code instead of
/// against a list - the approve gate itself, that approvable implies postable, and the status enum
/// the gates are written in terms of - because those are the ones a hand-mirror silently gets wrong.
/// </para>
/// </remarks>
public class SalesOrderActionsTests
{
    /// <summary>Every status the Web enum defines, so no theory below can quietly skip one.</summary>
    public static TheoryData<SalesOrderStatus> AllStatuses =>
    [
        SalesOrderStatus.Draft,
        SalesOrderStatus.Pending,
        SalesOrderStatus.Approved,
        SalesOrderStatus.PartiallyFulfilled,
        SalesOrderStatus.Fulfilled,
        SalesOrderStatus.Cancelled,
        SalesOrderStatus.OnHold,
        SalesOrderStatus.Rejected,
    ];

    /// <remarks>
    /// Draft belongs here as much as Pending, and this is the assertion the sales-order list did
    /// not agree with. A Web order is created as Draft and auto-posted in the same call; a refused
    /// post returns it to Draft with the reason recorded, so Draft is precisely where an order
    /// needing another attempt sits.
    /// </remarks>
    [Theory]
    [InlineData(SalesOrderStatus.Draft, true)]
    [InlineData(SalesOrderStatus.Pending, true)]
    [InlineData(SalesOrderStatus.Approved, false)]
    [InlineData(SalesOrderStatus.PartiallyFulfilled, false)]
    [InlineData(SalesOrderStatus.Fulfilled, false)]
    [InlineData(SalesOrderStatus.Cancelled, false)]
    [InlineData(SalesOrderStatus.OnHold, false)]
    [InlineData(SalesOrderStatus.Rejected, false)]
    public void Approve_matches_the_api_gate(SalesOrderStatus status, bool expected)
        => Assert.Equal(expected, SalesOrderActions.CanApprove(status));

    /// <remarks>
    /// The same set as approval today, but enforced by a different API method — UpdateAsync rather
    /// than ApproveAsync — so it is pinned separately. Editing is how a refused post gets fixed; if
    /// these two ever diverge, an order becomes fixable with nowhere to go, which is the shape the
    /// original bug had.
    /// </remarks>
    [Theory]
    [InlineData(SalesOrderStatus.Draft, true)]
    [InlineData(SalesOrderStatus.Pending, true)]
    [InlineData(SalesOrderStatus.Approved, false)]
    [InlineData(SalesOrderStatus.PartiallyFulfilled, false)]
    [InlineData(SalesOrderStatus.Fulfilled, false)]
    [InlineData(SalesOrderStatus.Cancelled, false)]
    [InlineData(SalesOrderStatus.OnHold, false)]
    [InlineData(SalesOrderStatus.Rejected, false)]
    public void Edit_matches_the_api_gate(SalesOrderStatus status, bool expected)
        => Assert.Equal(expected, SalesOrderActions.CanEdit(status));

    /// <remarks>
    /// Mirrors the transitions into Rejected that the API's IsValidStatusTransition permits: from
    /// Draft, Pending and OnHold and from nowhere else. An Approved order is past the point of
    /// rejection and gets cancelled instead.
    /// </remarks>
    [Theory]
    [InlineData(SalesOrderStatus.Draft, true)]
    [InlineData(SalesOrderStatus.Pending, true)]
    [InlineData(SalesOrderStatus.OnHold, true)]
    [InlineData(SalesOrderStatus.Approved, false)]
    [InlineData(SalesOrderStatus.PartiallyFulfilled, false)]
    [InlineData(SalesOrderStatus.Fulfilled, false)]
    [InlineData(SalesOrderStatus.Cancelled, false)]
    [InlineData(SalesOrderStatus.Rejected, false)]
    public void Reject_matches_the_api_gate(SalesOrderStatus status, bool expected)
        => Assert.Equal(expected, SalesOrderActions.CanReject(status));

    /// <remarks>
    /// The API also permits Rejected to Cancelled. Both pages already excluded it and merging them
    /// was not the moment to add an action neither had offered, so the exclusion is deliberate and
    /// pinned here rather than left looking like an oversight.
    /// </remarks>
    [Theory]
    [InlineData(SalesOrderStatus.Draft, true)]
    [InlineData(SalesOrderStatus.Pending, true)]
    [InlineData(SalesOrderStatus.Approved, true)]
    [InlineData(SalesOrderStatus.PartiallyFulfilled, true)]
    [InlineData(SalesOrderStatus.OnHold, true)]
    [InlineData(SalesOrderStatus.Fulfilled, false)]
    [InlineData(SalesOrderStatus.Cancelled, false)]
    [InlineData(SalesOrderStatus.Rejected, false)]
    public void Cancel_matches_what_both_pages_offered(SalesOrderStatus status, bool expected)
        => Assert.Equal(expected, SalesOrderActions.CanCancel(status));

    /// <remarks>
    /// Rejected is the status the two pages disagreed on: the sales-order list offered the action,
    /// the mobile list did not. The API permits Rejected to Draft, and a rejected order with no way
    /// back to Draft has no way forward at all, so the mobile list was the one that was wrong.
    /// Pending to Draft is legal on the API too, but neither page offered it and a Pending order
    /// already has Reject and Cancel.
    /// </remarks>
    [Theory]
    [InlineData(SalesOrderStatus.Cancelled, true)]
    [InlineData(SalesOrderStatus.Rejected, true)]
    [InlineData(SalesOrderStatus.Draft, false)]
    [InlineData(SalesOrderStatus.Pending, false)]
    [InlineData(SalesOrderStatus.Approved, false)]
    [InlineData(SalesOrderStatus.PartiallyFulfilled, false)]
    [InlineData(SalesOrderStatus.Fulfilled, false)]
    [InlineData(SalesOrderStatus.OnHold, false)]
    public void RestoreToDraft_matches_the_api_gate(SalesOrderStatus status, bool expected)
        => Assert.Equal(expected, SalesOrderActions.CanRestoreToDraft(status));

    /// <remarks>
    /// Exactly the set SalesOrderService.DeleteAsync enforces. Each page was missing one of it and
    /// the other page had that one — the sales-order list refused Pending, the mobile list refused
    /// Rejected — so an order the API would delete showed a delete button or not depending on which
    /// list it was viewed from.
    /// </remarks>
    [Theory]
    [InlineData(SalesOrderStatus.Draft, true)]
    [InlineData(SalesOrderStatus.Pending, true)]
    [InlineData(SalesOrderStatus.Cancelled, true)]
    [InlineData(SalesOrderStatus.Rejected, true)]
    [InlineData(SalesOrderStatus.Approved, false)]
    [InlineData(SalesOrderStatus.PartiallyFulfilled, false)]
    [InlineData(SalesOrderStatus.Fulfilled, false)]
    [InlineData(SalesOrderStatus.OnHold, false)]
    public void Delete_matches_the_api_gate(SalesOrderStatus status, bool expected)
        => Assert.Equal(expected, SalesOrderActions.CanDelete(status));

    /// <summary>
    /// The Web's approve gate must be the API's approve gate, status for status.
    /// </summary>
    /// <remarks>
    /// Checked against SalesOrderService.CanApprove itself, not against a copy of its list. The
    /// two projects share no code, so this is the assertion that makes the Web copy a mirror
    /// rather than a guess: the page cannot offer approval the API would refuse, and cannot
    /// withhold it where the API would accept - which is the failure that stranded Draft orders.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Approve_agrees_with_the_api_status_for_status(SalesOrderStatus status)
        => Assert.Equal(
            ApiSalesOrderService.CanApprove((ApiStatus)status),
            SalesOrderActions.CanApprove(status));

    /// <summary>
    /// Approving posts to SAP, so anything the Web offers approval on must be a status the API's
    /// posting gate accepts — otherwise approval would move the order to a status posting then
    /// refuses, and it would strand a second time.
    /// </summary>
    /// <remarks>
    /// The API pins this for its own gate in SalesOrderSafetyTests. Pinned again from the Web side
    /// because the Web gate is a separate copy: the two could agree with the posting rule
    /// individually and still be reachable only through the page.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void Anything_approvable_is_postable(SalesOrderStatus status)
    {
        if (!SalesOrderActions.CanApprove(status))
            return;

        Assert.True(ApiSalesOrderService.CanPostToSap((ApiStatus)status));
    }

    /// <summary>
    /// The Web status enum is itself a hand-mirrored copy of the API's, and every gate above is
    /// written in terms of it. A status renumbered on one side and not the other would silently
    /// repoint each gate at a different status, so the mirror is checked at its foundation.
    /// </summary>
    /// <remarks>
    /// Name and number both: the DTO is deserialized from a JSON number, so the number is what
    /// actually arrives, and the name is what every gate is written against. The Web enum carries
    /// one name the API does not — Invoiced, an alias for Fulfilled — so the check is that the
    /// API's name is among the Web's names for that number, not that it is the only one.
    /// </remarks>
    [Fact]
    public void Web_status_enum_mirrors_the_api_enum()
    {
        foreach (var apiStatus in Enum.GetValues<ApiStatus>())
        {
            var name = Enum.GetName(apiStatus)!;
            var number = (int)apiStatus;

            Assert.True(
                Enum.IsDefined((SalesOrderStatus)number),
                $"The API has {name} = {number} and the Web enum has no such value.");

            var webNamesForNumber = Enum.GetNames<SalesOrderStatus>()
                .Where(webName => (int)Enum.Parse<SalesOrderStatus>(webName) == number)
                .ToList();

            Assert.Contains(name, webNamesForNumber);
        }
    }

    /// <summary>
    /// An order that reached SAP is SAP's, and every action here is local bookkeeping that would
    /// leave the two systems disagreeing. Both pages applied this guard to every gate by hand; it
    /// lives in the helper now, and this is what holds it there for gates added later.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void A_synced_order_offers_nothing(SalesOrderStatus status)
    {
        var order = new SalesOrderDto { Status = status, IsSynced = true };

        Assert.False(SalesOrderActions.CanApprove(order));
        Assert.False(SalesOrderActions.CanEdit(order));
        Assert.False(SalesOrderActions.CanReject(order));
        Assert.False(SalesOrderActions.CanCancel(order));
        Assert.False(SalesOrderActions.CanRestoreToDraft(order));
        Assert.False(SalesOrderActions.CanDelete(order));
    }

    /// <summary>
    /// The DTO overloads are what the pages actually call, so each must be its status counterpart
    /// plus the synced guard and nothing else.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void An_open_order_offers_exactly_what_its_status_allows(SalesOrderStatus status)
    {
        var order = new SalesOrderDto { Status = status, IsSynced = false };

        Assert.Equal(SalesOrderActions.CanApprove(status), SalesOrderActions.CanApprove(order));
        Assert.Equal(SalesOrderActions.CanEdit(status), SalesOrderActions.CanEdit(order));
        Assert.Equal(SalesOrderActions.CanReject(status), SalesOrderActions.CanReject(order));
        Assert.Equal(SalesOrderActions.CanCancel(status), SalesOrderActions.CanCancel(order));
        Assert.Equal(SalesOrderActions.CanRestoreToDraft(status), SalesOrderActions.CanRestoreToDraft(order));
        Assert.Equal(SalesOrderActions.CanDelete(status), SalesOrderActions.CanDelete(order));
    }
}
