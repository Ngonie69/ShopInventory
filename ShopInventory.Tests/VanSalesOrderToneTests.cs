using ShopInventory.Web.Components.Pages;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// The words and the colours /van-sales-customer-orders tells a delivery status in.
///
/// The rule these pin: one status has one label and one tone, and the filter's swatch and the badge
/// on the order below it both read them from here. The failure mode is not a crash — it is a filter
/// row that is amber next to a badge that is grey for the same order, which nobody notices until
/// someone is reading the page to answer a shop. The tones were revised twice while this page was
/// being converted, so they are a judgement worth writing down rather than a detail.
///
/// They also cannot be seen locally: the development database holds no customer orders, so no badge
/// ever renders and driving the page proves the filter's half only. That is the whole reason this
/// file exists.
/// </summary>
public sealed class VanSalesOrderToneTests
{
    /// <summary>
    /// Neutral, not accent: an order awaiting delivery is doing exactly what it should, and a
    /// colour here would compete with the rows that have earned one.
    /// </summary>
    [Fact]
    public void An_order_awaiting_delivery_is_told_plainly()
    {
        Assert.Equal("neutral", VanSalesCustomerOrders.StatusFamily(VanSalesOrderStatusModel.Accepted));
        Assert.Equal("Awaiting delivery", VanSalesCustomerOrders.StatusLabel(VanSalesOrderStatusModel.Accepted));
    }

    [Fact]
    public void A_delivered_order_is_the_only_good_one()
    {
        Assert.Equal("good", VanSalesCustomerOrders.StatusFamily(VanSalesOrderStatusModel.Fulfilled));
    }

    /// <summary>
    /// Both are orders that did not go the way they were placed, so they share warn. Kept in one
    /// test because the pairing is the point: splitting them invites one to be changed alone.
    /// </summary>
    [Theory]
    [InlineData(VanSalesOrderStatusModel.PartiallyFulfilled)]
    [InlineData(VanSalesOrderStatusModel.Cancelled)]
    public void An_order_that_went_wrong_is_worth_an_eye(VanSalesOrderStatusModel status)
    {
        Assert.Equal("warn", VanSalesCustomerOrders.StatusFamily(status));
    }

    /// <summary>
    /// Bad is reserved. An expired order is the one outcome nobody chose — the shop did not cancel
    /// it and the van did not deliver it — so it is the only status that should pull an eye across
    /// the page on its own.
    /// </summary>
    [Fact]
    public void Only_an_expired_order_is_bad()
    {
        var bad = AllStatuses()
            .Where(status => VanSalesCustomerOrders.StatusFamily(status) == "bad")
            .ToList();

        Assert.Equal([VanSalesOrderStatusModel.Expired], bad);
    }

    /// <summary>
    /// Every status has to answer, and the answer has to be a family the stylesheet defines —
    /// `ops-fam-<c>whatever</c>` on a badge is silently no colour at all, not an error.
    /// </summary>
    [Fact]
    public void Every_status_maps_to_a_family_the_sheet_defines()
    {
        string[] families = ["neutral", "accent", "info", "good", "warn", "bad"];

        foreach (var status in AllStatuses())
        {
            Assert.Contains(VanSalesCustomerOrders.StatusFamily(status), families);
        }
    }

    /// <summary>
    /// The words themselves, pinned. These are read by someone being asked about the order by a
    /// shop, so they are the shop's words rather than the model's — "Part delivered", not
    /// "PartiallyFulfilled"; "Not delivered", not "Expired". One comes out the same as its enum
    /// member, and that is correct: "Cancelled" is already what an operator would say.
    /// </summary>
    [Fact]
    public void Every_status_is_told_in_the_shops_words()
    {
        Dictionary<VanSalesOrderStatusModel, string> expected = new()
        {
            [VanSalesOrderStatusModel.Accepted] = "Awaiting delivery",
            [VanSalesOrderStatusModel.Fulfilled] = "Delivered",
            [VanSalesOrderStatusModel.PartiallyFulfilled] = "Part delivered",
            [VanSalesOrderStatusModel.Cancelled] = "Cancelled",
            [VanSalesOrderStatusModel.Expired] = "Not delivered"
        };

        Assert.Equal(expected.Keys.Order(), AllStatuses().Order());
        Assert.Equal(expected, AllStatuses().ToDictionary(s => s, VanSalesCustomerOrders.StatusLabel));
    }

    private static IEnumerable<VanSalesOrderStatusModel> AllStatuses() =>
        Enum.GetValues<VanSalesOrderStatusModel>();
}
