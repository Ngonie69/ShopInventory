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
}
