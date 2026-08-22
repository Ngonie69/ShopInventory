using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

public class SalesOrderSafetyTests
{
    [Theory]
    [InlineData(SalesOrderStatus.Draft)]
    [InlineData(SalesOrderStatus.Pending)]
    [InlineData(SalesOrderStatus.Approved)]
    public void Postable_statuses_are_allowed(SalesOrderStatus status)
    {
        Assert.True(SalesOrderService.CanPostToSap(status));
    }

    [Theory]
    [InlineData(SalesOrderStatus.Cancelled)]
    [InlineData(SalesOrderStatus.Rejected)]
    [InlineData(SalesOrderStatus.OnHold)]
    [InlineData(SalesOrderStatus.PartiallyFulfilled)]
    [InlineData(SalesOrderStatus.Fulfilled)]
    public void Terminal_or_non_postable_statuses_are_rejected(SalesOrderStatus status)
    {
        Assert.False(SalesOrderService.CanPostToSap(status));
    }

    /// <summary>
    /// Draft is approvable, and this is the assertion the sales order list has to agree with.
    /// </summary>
    /// <remarks>
    /// A Web order is created as Draft and auto-posted in the same call. A refused post - an
    /// unpriced line, a credit hold, SAP unreachable - returns it to Draft with the reason
    /// recorded. The list offered approval on Pending alone, so those orders had no action left:
    /// the cause could be edited away and nothing would retry the post. The gate lives here now so
    /// the two cannot say different things again.
    /// </remarks>
    [Theory]
    [InlineData(SalesOrderStatus.Draft)]
    [InlineData(SalesOrderStatus.Pending)]
    public void Approvable_statuses_are_allowed(SalesOrderStatus status)
    {
        Assert.True(SalesOrderService.CanApprove(status));
    }

    [Theory]
    [InlineData(SalesOrderStatus.Approved)]
    [InlineData(SalesOrderStatus.Cancelled)]
    [InlineData(SalesOrderStatus.Rejected)]
    [InlineData(SalesOrderStatus.OnHold)]
    [InlineData(SalesOrderStatus.PartiallyFulfilled)]
    [InlineData(SalesOrderStatus.Fulfilled)]
    public void Non_approvable_statuses_are_rejected(SalesOrderStatus status)
    {
        Assert.False(SalesOrderService.CanApprove(status));
    }

    /// <summary>
    /// Anything approvable must also be postable, or approval would take an order to a status the
    /// posting gate then refuses and the order would strand a second time.
    /// </summary>
    [Theory]
    [InlineData(SalesOrderStatus.Draft)]
    [InlineData(SalesOrderStatus.Pending)]
    public void Approving_never_leads_to_a_status_posting_refuses(SalesOrderStatus status)
    {
        Assert.True(SalesOrderService.CanApprove(status) && SalesOrderService.CanPostToSap(status));
    }
}
