using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesOrders.Commands.RecordVanSalesOrderDelivery;

/// <summary>
/// Records what the van actually handed over, and closes the order accordingly.
/// </summary>
/// <remarks>
/// A line not mentioned in the request is left alone rather than zeroed. A rep recording the two
/// lines they were short on should not thereby declare that nothing else was delivered, and a
/// partial submission from a handset that lost connection halfway must not be read as a complete
/// one.
/// <para>
/// The resulting status is derived from the quantities rather than supplied. Letting the caller say
/// "Fulfilled" while sending short quantities would put a claim in the record that its own lines
/// contradict — and the whole value of this step is that the customer and the supplier are reading
/// the same number.
/// </para>
/// </remarks>
public sealed class RecordVanSalesOrderDeliveryHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    IVanSalesCustomerNotifier notifier,
    ILogger<RecordVanSalesOrderDeliveryHandler> logger)
    : IRequestHandler<RecordVanSalesOrderDeliveryCommand, ErrorOr<VanSalesOrderResult>>
{
    public async Task<ErrorOr<VanSalesOrderResult>> Handle(
        RecordVanSalesOrderDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        var order = await context.VanSalesOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);

        if (order is null)
        {
            return Errors.VanSalesOrders.NotFound;
        }

        if (order.Status == VanSalesOrderStatus.Cancelled)
        {
            return Errors.VanSalesOrders.AlreadyCancelled;
        }

        var byLineNumber = order.Lines.ToDictionary(l => l.LineNumber);

        var unknown = command.Lines
            .Where(l => !byLineNumber.ContainsKey(l.LineNumber))
            .Select(l => l.LineNumber)
            .ToList();

        if (unknown.Count > 0)
        {
            return Errors.VanSalesOrders.UnknownLines(unknown);
        }

        var over = command.Lines
            .Where(l => l.QuantityFulfilled > byLineNumber[l.LineNumber].QuantityOrdered)
            .Select(l => byLineNumber[l.LineNumber].ItemCode)
            .ToList();

        if (over.Count > 0)
        {
            // Delivering more than was ordered is a different transaction — an upsell at the door —
            // and belongs on an invoice the rep raises, not silently inflated onto the order the
            // customer placed and can see.
            return Errors.VanSalesOrders.OverDelivered(over);
        }

        var now = DateTime.UtcNow;

        foreach (var line in command.Lines)
        {
            byLineNumber[line.LineNumber].QuantityFulfilled = line.QuantityFulfilled;
        }

        order.Status = DeriveStatus(order.Lines);
        order.DeliveredAtUtc = now;
        order.UpdatedAt = now;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else moved this order between the read and the write — most likely the
            // customer cancelling as the rep arrived. Reporting it beats overwriting a
            // cancellation with a delivery nobody agreed to.
            logger.LogWarning(
                "Van sales order {OrderNumber} changed while its delivery was being recorded.",
                order.OrderNumber);

            return Errors.VanSalesOrders.ChangedElsewhere;
        }

        try
        {
            await auditService.LogAsync(
                AuditActions.RecordVanSalesCustomerOrderDelivery,
                "VanSalesOrder",
                order.Id.ToString(),
                $"Delivery recorded against {order.OrderNumber} for {order.RouteCustomerCode}: {order.Status}.",
                true);
        }
        catch
        {
            // Auditing must not cost the rep the delivery they just recorded.
        }

        // After the save, never before. A push telling a shopkeeper their order was short
        // delivered, sent for a change that then failed to commit, is worse than no push at all.
        await notifier.NotifyOrderStatusAsync(order, cancellationToken);

        logger.LogInformation(
            "Recorded delivery against van sales order {OrderNumber}: {Status}.",
            order.OrderNumber,
            order.Status);

        return VanSalesOrderProjection.ToResultInMemory(order);
    }

    /// <summary>
    /// The order's status, read off the quantities.
    /// </summary>
    /// <remarks>
    /// Nothing delivered at all is <see cref="VanSalesOrderStatus.Expired"/> rather than Fulfilled
    /// with zeroes: the van came and the shop got nothing, and calling that fulfilled would hide
    /// the very failure worth counting.
    /// </remarks>
    private static VanSalesOrderStatus DeriveStatus(ICollection<VanSalesOrderLineEntity> lines)
    {
        if (lines.All(l => l.QuantityFulfilled <= 0))
        {
            return VanSalesOrderStatus.Expired;
        }

        return lines.All(l => l.QuantityFulfilled >= l.QuantityOrdered)
            ? VanSalesOrderStatus.Fulfilled
            : VanSalesOrderStatus.PartiallyFulfilled;
    }
}
