using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesOrders.Commands.CancelVanSalesCustomerOrder;

/// <summary>
/// Withdraws an order, if the van has not been loaded for it yet.
/// </summary>
/// <remarks>
/// Scoped to the caller's own orders by the query itself rather than by a check afterwards, and
/// another shop's order is reported as not found rather than forbidden — the two answers together
/// would let a signed-in customer walk the id range and count a competitor's orders.
/// <para>
/// The cut-off does double duty: it is the deadline for placing an order and the deadline for
/// taking one back. Past it the stock has been picked for this shop, so a cancellation is a
/// conversation with the rep rather than a button, and saying it worked when it did not is how a
/// shop comes to refuse a delivery at the door.
/// </para>
/// </remarks>
public sealed class CancelVanSalesCustomerOrderHandler(
    ApplicationDbContext context,
    IVanSalesOrderingPolicy orderingPolicy,
    IAuditService auditService,
    IVanSalesCustomerNotifier notifier,
    ILogger<CancelVanSalesCustomerOrderHandler> logger)
    : IRequestHandler<CancelVanSalesCustomerOrderCommand, ErrorOr<VanSalesOrderResult>>
{
    public async Task<ErrorOr<VanSalesOrderResult>> Handle(
        CancelVanSalesCustomerOrderCommand command,
        CancellationToken cancellationToken)
    {
        var order = await context.VanSalesOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(
                o => o.Id == command.OrderId
                     && o.VanSalesCustomerAccountId == command.AccountId,
                cancellationToken);

        if (order is null)
        {
            return Errors.VanSalesOrders.NotFound;
        }

        if (order.Status == VanSalesOrderStatus.Cancelled)
        {
            // Reported rather than shrugged off. A handset retrying a cancellation it already got
            // through is harmless, but a customer tapping cancel on a screen that never refreshed
            // deserves to know why nothing changed.
            return Errors.VanSalesOrders.AlreadyCancelled;
        }

        if (order.Status != VanSalesOrderStatus.Accepted)
        {
            return Errors.VanSalesOrders.CannotCancel;
        }

        if (order.RequestedVisitDate is { } visitDate)
        {
            var rules = await orderingPolicy.GetRulesAsync(cancellationToken);
            var closesAt = VanSalesVisitSchedule.CutOffUtc(visitDate, rules.CutOffHoursBeforeVisitDay);

            if (DateTime.UtcNow >= closesAt)
            {
                logger.LogInformation(
                    "Refused to cancel van sales order {OrderNumber}: the cut-off for {VisitDate:yyyy-MM-dd} passed at {ClosesAt:O}.",
                    order.OrderNumber,
                    visitDate,
                    closesAt);

                return Errors.VanSalesOrders.CancellationWindowClosed;
            }
        }

        var now = DateTime.UtcNow;

        order.Status = VanSalesOrderStatus.Cancelled;
        order.CancelledAtUtc = now;
        order.CancellationReason = command.Reason;
        order.UpdatedAt = now;

        await context.SaveChangesAsync(cancellationToken);

        try
        {
            await auditService.LogAsync(
                AuditActions.CancelVanSalesCustomerOrder,
                "VanSalesOrder",
                order.Id.ToString(),
                $"Order {order.OrderNumber} cancelled by {order.RouteCustomerCode}.",
                true);
        }
        catch
        {
            // Auditing must not cost the customer the cancellation they just made.
        }

        // Sent even though the customer did this themselves: they may have two handsets, and the
        // other one is still showing the order as coming.
        await notifier.NotifyOrderStatusAsync(order, cancellationToken);

        logger.LogInformation(
            "Van sales customer order {OrderNumber} cancelled by {Customer}.",
            order.OrderNumber,
            order.RouteCustomerCode);

        return VanSalesOrderProjection.ToResultInMemory(order);
    }
}
