using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Merchandiser.Commands.BackfillMobileOrderTax;

public sealed class BackfillMobileOrderTaxHandler(
    ApplicationDbContext context,
    IOptions<RevmaxSettings> revmaxSettings,
    ILogger<BackfillMobileOrderTaxHandler> logger
) : IRequestHandler<BackfillMobileOrderTaxCommand, ErrorOr<BackfillMobileOrderTaxResult>>
{
    private readonly decimal _defaultTaxPercent = NormalizeTaxPercent(revmaxSettings.Value.VatRate);

    public async Task<ErrorOr<BackfillMobileOrderTaxResult>> Handle(
        BackfillMobileOrderTaxCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var orders = await context.SalesOrders
                .AsTracking()
                .Include(order => order.Lines)
                .Where(order => order.Source == SalesOrderSource.Mobile
                    && order.TaxAmount <= 0
                    && (order.Status == SalesOrderStatus.Approved
                        || order.Status == SalesOrderStatus.PartiallyFulfilled
                        || order.Status == SalesOrderStatus.Fulfilled)
                    && order.Lines.Any(line => line.TaxPercent <= 0))
                .ToListAsync(cancellationToken);

            if (orders.Count == 0)
            {
                return new BackfillMobileOrderTaxResult(0, 0, _defaultTaxPercent);
            }

            var ordersUpdated = 0;
            var linesUpdated = 0;

            foreach (var order in orders)
            {
                var orderLineUpdates = 0;

                foreach (var line in order.Lines)
                {
                    if (line.TaxPercent > 0)
                        continue;

                    line.TaxPercent = _defaultTaxPercent;
                    orderLineUpdates++;
                }

                if (orderLineUpdates == 0)
                    continue;

                order.SubTotal = order.Lines.Sum(line => line.LineTotal);
                order.TaxAmount = Math.Round(order.Lines.Sum(line => line.LineTotal * line.TaxPercent / 100m), 2);
                order.DiscountAmount = Math.Round(order.SubTotal * order.DiscountPercent / 100m, 2);
                order.DocTotal = Math.Round(order.SubTotal - order.DiscountAmount + order.TaxAmount, 2);
                order.UpdatedAt = DateTime.UtcNow;

                ordersUpdated++;
                linesUpdated += orderLineUpdates;
            }

            if (ordersUpdated == 0)
            {
                return new BackfillMobileOrderTaxResult(0, 0, _defaultTaxPercent);
            }

            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Backfilled tax for {OrdersUpdated} approved mobile sales orders and {LinesUpdated} lines using VAT {TaxPercent}",
                ordersUpdated,
                linesUpdated,
                _defaultTaxPercent);

            return new BackfillMobileOrderTaxResult(ordersUpdated, linesUpdated, _defaultTaxPercent);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to backfill tax for approved mobile sales orders");
            return Error.Failure("Merchandiser.BackfillMobileOrderTaxFailed", ex.GetBaseException().Message);
        }
    }

    private static decimal NormalizeTaxPercent(decimal configuredVatRate)
        => configuredVatRate <= 1 ? configuredVatRate * 100m : configuredVatRate;
}