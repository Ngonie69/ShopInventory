using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.BackfillSalesOrderCardNames;

public sealed class BackfillSalesOrderCardNamesHandler(
    ApplicationDbContext context,
    IBusinessPartnerService businessPartnerService,
    ILogger<BackfillSalesOrderCardNamesHandler> logger
) : IRequestHandler<BackfillSalesOrderCardNamesCommand, ErrorOr<BackfillSalesOrderCardNamesResult>>
{
    public async Task<ErrorOr<BackfillSalesOrderCardNamesResult>> Handle(
        BackfillSalesOrderCardNamesCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var orders = await context.SalesOrders
                .AsTracking()
                .Where(order => order.CardCode != null &&
                    (order.CardName == null || order.CardName == string.Empty || order.CardName == order.CardCode))
                .ToListAsync(cancellationToken);

            if (orders.Count == 0)
            {
                return new BackfillSalesOrderCardNamesResult(0, 0, 0);
            }

            var updatedAt = DateTime.UtcNow;
            var ordersUpdated = 0;
            var customersResolved = 0;
            var customersUnresolved = 0;

            foreach (var cardCodeGroup in orders
                .Where(order => !string.IsNullOrWhiteSpace(order.CardCode))
                .GroupBy(order => order.CardCode!.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var businessPartner = await businessPartnerService.GetBusinessPartnerByCodeAsync(cardCodeGroup.Key, cancellationToken);
                var resolvedCardName = businessPartner?.CardName?.Trim();

                if (string.IsNullOrWhiteSpace(resolvedCardName) ||
                    string.Equals(resolvedCardName, cardCodeGroup.Key, StringComparison.OrdinalIgnoreCase))
                {
                    customersUnresolved++;
                    continue;
                }

                customersResolved++;

                foreach (var order in cardCodeGroup)
                {
                    order.CardName = resolvedCardName;
                    order.UpdatedAt = updatedAt;
                    ordersUpdated++;
                }
            }

            if (ordersUpdated > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            logger.LogInformation(
                "Backfilled sales order customer names for {OrdersUpdated} orders across {CustomersResolved} customers; {CustomersUnresolved} customers could not be resolved",
                ordersUpdated,
                customersResolved,
                customersUnresolved);

            return new BackfillSalesOrderCardNamesResult(ordersUpdated, customersResolved, customersUnresolved);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to backfill sales order customer names");
            return Errors.SalesOrder.BackfillFailed(ex.GetBaseException().Message);
        }
    }
}