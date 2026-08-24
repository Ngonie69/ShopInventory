using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesOrders;

/// <summary>Tells a customer's handset what has happened to their order.</summary>
public interface IVanSalesCustomerNotifier
{
    /// <summary>
    /// Push an order's new status to every live device of the customer that placed it.
    /// </summary>
    /// <remarks>
    /// Never throws. A push that does not arrive is a customer who checks the app instead; a push
    /// that throws would roll back the delivery record or the cancellation that prompted it, which
    /// is a far worse outcome than a missed notification.
    /// </remarks>
    Task NotifyOrderStatusAsync(VanSalesOrderEntity order, CancellationToken cancellationToken);
}
