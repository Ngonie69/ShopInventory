using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesOrders.Commands.ConvertVanSalesOrderToSalesOrder;

/// <summary>
/// Creates a sales order from a customer's order, and records the link.
/// </summary>
/// <remarks>
/// The whole reason the intake is a separate table. Everything up to here is a request from a shop;
/// this is where it becomes a document the ERP will act on, and it happens once, deliberately, with
/// a named user against it.
/// <para>
/// The resulting sales order is created with <see cref="SalesOrderSource.VanSalesCustomer"/>, which
/// is neither <c>Web</c> nor <c>Mobile</c>, and that placement was checked rather than assumed. It
/// means the credit limit is enforced here — where a person is looking and can act on it — the
/// merchandiser post-processing queue is left alone, staff get the usual notification, and the
/// order is <em>not</em> auto-posted to SAP. It lands as a Draft for the normal approval flow,
/// because a customer's request becoming an ERP document is a smaller decision than that document
/// reaching SAP unattended.
/// </para>
/// <para>
/// <c>CardCode</c> is the van's business partner, not the shop's. That is how van sales orders work
/// throughout this system — the shop is carried in the route customer fields — and getting it the
/// other way round would post the order against a business partner SAP does not trade with.
/// </para>
/// </remarks>
public sealed class ConvertVanSalesOrderToSalesOrderHandler(
    ApplicationDbContext context,
    ISalesOrderService salesOrderService,
    IAuditService auditService,
    ILogger<ConvertVanSalesOrderToSalesOrderHandler> logger)
    : IRequestHandler<ConvertVanSalesOrderToSalesOrderCommand, ErrorOr<VanSalesOrderConversionResult>>
{
    public async Task<ErrorOr<VanSalesOrderConversionResult>> Handle(
        ConvertVanSalesOrderToSalesOrderCommand command,
        CancellationToken cancellationToken)
    {
        var order = await context.VanSalesOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == command.OrderId, cancellationToken);

        if (order is null)
        {
            return Errors.VanSalesOrders.NotFound;
        }

        if (order.ConvertedSalesOrderId is not null)
        {
            // Reported rather than repeated. Converting twice would put the same goods on two
            // documents, and the second would be invisible to whoever raised the first.
            return Errors.VanSalesOrders.AlreadyConverted(order.OrderNumber);
        }

        if (order.Status != VanSalesOrderStatus.Accepted)
        {
            return Errors.VanSalesOrders.NotConvertible;
        }

        if (string.IsNullOrWhiteSpace(order.AssignedBusinessPartnerCode))
        {
            // Without the van's business partner there is nothing to post against. Refused with a
            // reason rather than posted against a guess.
            return Errors.VanSalesOrders.ConversionFailed(
                $"Order {order.OrderNumber} has no business partner recorded, so it cannot be converted.");
        }

        var request = new CreateSalesOrderRequest
        {
            CardCode = order.AssignedBusinessPartnerCode,
            CardName = order.RouteName,
            RouteCustomerId = order.RouteCustomerId,
            RouteCustomerCode = order.RouteCustomerCode,
            RouteCustomerName = order.RouteCustomerName,
            DeliveryDate = order.RequestedVisitDate,
            Comments = order.CustomerNotes,
            Currency = order.Currency,
            Source = SalesOrderSource.VanSalesCustomer,

            // The intake's own key, carried across. It makes the conversion itself idempotent at
            // the sales order layer: a second attempt that slips past the check above collides on
            // the sales order's unique index rather than creating a duplicate document.
            ClientRequestId = order.ClientRequestId,

            DeviceInfo = order.DeviceInfo,
            Lines = order.Lines
                .OrderBy(l => l.LineNumber)
                .Select(l => new CreateSalesOrderLineRequest
                {
                    ItemCode = l.ItemCode,
                    ItemDescription = l.ItemDescription,
                    Quantity = l.QuantityOrdered,
                    UnitPrice = l.UnitPrice,
                    TaxPercent = l.TaxPercent,
                    UoMCode = l.UoMCode
                })
                .ToList()
        };

        SalesOrderDto created;

        try
        {
            created = await salesOrderService.CreateAsync(request, command.UserId, cancellationToken);
        }
        catch (CreditLimitExceededException ex)
        {
            // Surfaced rather than swallowed. Unlike a rep in the field, whoever is converting can
            // act on this — take a payment, raise the limit, or leave the order unconverted — and
            // the order stays exactly where it was in the meantime.
            logger.LogWarning(
                "Refused to convert van sales order {OrderNumber} on credit: {Reason}",
                order.OrderNumber,
                ex.Message);

            return Errors.SalesOrder.CreditLimitExceeded(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Converting van sales order {OrderNumber} failed.", order.OrderNumber);
            return Errors.VanSalesOrders.ConversionFailed(ex.InnerException?.Message ?? ex.Message);
        }

        var now = DateTime.UtcNow;

        order.ConvertedSalesOrderId = created.Id;
        order.ConvertedAtUtc = now;
        order.UpdatedAt = now;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The sales order exists but the link could not be written. Reported loudly, because
            // the two are now out of step and a second conversion attempt would duplicate the
            // document rather than being caught by the check above.
            logger.LogError(
                "Created sales order {SalesOrderNumber} from van sales order {OrderNumber}, but the link could not be saved.",
                created.OrderNumber,
                order.OrderNumber);

            return Errors.VanSalesOrders.ConversionFailed(
                $"Sales order {created.OrderNumber} was created, but this order could not be marked as converted. Check before converting it again.");
        }

        try
        {
            await auditService.LogAsync(
                AuditActions.ConvertVanSalesCustomerOrder,
                "VanSalesOrder",
                order.Id.ToString(),
                $"Order {order.OrderNumber} for {order.RouteCustomerCode} converted to sales order {created.OrderNumber}.",
                true);
        }
        catch
        {
            // Auditing must not undo a conversion that has already happened.
        }

        logger.LogInformation(
            "Converted van sales order {OrderNumber} to sales order {SalesOrderNumber}.",
            order.OrderNumber,
            created.OrderNumber);

        return new VanSalesOrderConversionResult(
            order.Id,
            order.OrderNumber,
            created.Id,
            created.OrderNumber);
    }
}
