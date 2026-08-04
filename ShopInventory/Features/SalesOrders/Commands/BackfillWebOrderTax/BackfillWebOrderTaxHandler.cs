using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Commands.BackfillWebOrderTax;

/// <summary>
/// Repairs web-created sales orders that were saved with no tax at all.
/// </summary>
/// <remarks>
/// The create form never sent a line tax rate, and the API only substitutes the configured VAT rate
/// for mobile orders, so every web order was persisted with zero-rate lines, a zero tax amount, and
/// a document total equal to its subtotal. SAP prices the tax itself from each item's tax code, so
/// the posted documents are correct and it is only the local mirror that is short.
///
/// That difference in where the truth lives is why the two groups are repaired differently:
/// <list type="bullet">
/// <item>An order SAP has never seen has no other source than configuration, so it is recomputed at
/// the configured VAT rate — the same treatment the mobile backfill applies, and the same number a
/// newly created order now carries.</item>
/// <item>An order SAP already holds is rewritten from that document. Recomputing it at the
/// configured rate would be right only for an order whose lines are all standard-rated, and would
/// replace a total that is known to be wrong with one that is quietly wrong for anything
/// zero-rated or exempt.</item>
/// </list>
///
/// Cancelled and rejected orders are left alone. Their totals no longer feed a decision, and
/// rewriting a closed document's money is more surprising than leaving it as it was recorded.
/// </remarks>
public sealed class BackfillWebOrderTaxHandler(
    ApplicationDbContext context,
    ISalesOrderService salesOrderService,
    IOptions<RevmaxSettings> revmaxSettings,
    ILogger<BackfillWebOrderTaxHandler> logger
) : IRequestHandler<BackfillWebOrderTaxCommand, ErrorOr<BackfillWebOrderTaxResult>>
{
    private readonly decimal _configuredTaxPercent = NormalizeTaxPercent(revmaxSettings.Value.VatRate);

    public async Task<ErrorOr<BackfillWebOrderTaxResult>> Handle(
        BackfillWebOrderTaxCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await context.SalesOrders
                .AsTracking()
                .Include(order => order.Lines)
                .Where(order => order.Source == SalesOrderSource.Web
                    && order.TaxAmount <= 0
                    && order.Status != SalesOrderStatus.Cancelled
                    && order.Status != SalesOrderStatus.Rejected
                    && order.Lines.Any(line => line.TaxPercent <= 0))
                .ToListAsync(cancellationToken);

            if (candidates.Count == 0)
            {
                return EmptyResult(command.DryRun);
            }

            var postedOrderIds = candidates
                .Where(IsPostedToSap)
                .Select(order => order.Id)
                .ToList();

            var unpostedOrders = candidates
                .Where(order => !IsPostedToSap(order))
                .ToList();

            var (unpostedOrdersUpdated, unpostedLinesUpdated) = RepairUnpostedOrders(unpostedOrders, command.DryRun);

            if (!command.DryRun && unpostedOrdersUpdated > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            var postedOrderIdsThisRun = postedOrderIds
                .Take(Math.Max(command.MaxPostedOrders, 0))
                .ToList();

            var postedRepair = postedOrderIdsThisRun.Count == 0
                ? new SalesOrderTaxRepairSummary(0, 0, 0)
                : await salesOrderService.RepairSyncedSalesOrderTaxFromSapAsync(
                    postedOrderIdsThisRun,
                    command.DryRun,
                    cancellationToken);

            var result = new BackfillWebOrderTaxResult(
                OrdersAffected: candidates.Count,
                UnpostedOrdersFound: unpostedOrders.Count,
                UnpostedOrdersUpdated: unpostedOrdersUpdated,
                UnpostedLinesUpdated: unpostedLinesUpdated,
                PostedOrdersFound: postedOrderIds.Count,
                PostedOrdersQueried: postedOrderIdsThisRun.Count,
                PostedOrdersRepaired: postedRepair.OrdersRepaired,
                PostedLinesRepaired: postedRepair.LinesRepaired,
                PostedOrdersUnresolved: postedRepair.OrdersUnresolved,
                PostedOrdersRemaining: postedOrderIds.Count - postedOrderIdsThisRun.Count,
                ConfiguredTaxPercent: _configuredTaxPercent,
                DryRun: command.DryRun);

            logger.LogInformation(
                "Web sales order tax backfill ({Mode}): {OrdersAffected} affected order(s); "
                + "unposted {UnpostedUpdated}/{UnpostedFound} order(s) and {UnpostedLines} line(s) set to VAT {TaxPercent}; "
                + "posted {PostedRepaired}/{PostedQueried} order(s) and {PostedLines} line(s) rewritten from SAP, "
                + "{PostedUnresolved} unresolved, {PostedRemaining} left for a later run.",
                command.DryRun ? "dry run" : "applied",
                result.OrdersAffected,
                result.UnpostedOrdersUpdated,
                result.UnpostedOrdersFound,
                result.UnpostedLinesUpdated,
                _configuredTaxPercent,
                result.PostedOrdersRepaired,
                result.PostedOrdersQueried,
                result.PostedLinesRepaired,
                result.PostedOrdersUnresolved,
                result.PostedOrdersRemaining);

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to backfill tax for web sales orders");
            return Errors.SalesOrder.BackfillFailed(ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Recomputes the orders SAP has never seen from the configured VAT rate, mirroring
    /// <c>CalculateSalesOrderTotals</c> so a repaired order carries the totals it would have been
    /// created with today.
    /// </summary>
    private (int OrdersUpdated, int LinesUpdated) RepairUnpostedOrders(
        IReadOnlyCollection<SalesOrderEntity> orders,
        bool dryRun)
    {
        var ordersUpdated = 0;
        var linesUpdated = 0;
        var updatedAt = DateTime.UtcNow;

        foreach (var order in orders)
        {
            var orderLineUpdates = order.Lines.Count(line => line.TaxPercent <= 0);
            if (orderLineUpdates == 0)
                continue;

            ordersUpdated++;
            linesUpdated += orderLineUpdates;

            if (dryRun)
                continue;

            foreach (var line in order.Lines)
            {
                if (line.TaxPercent > 0)
                    continue;

                line.TaxPercent = _configuredTaxPercent;
            }

            order.SubTotal = order.Lines.Sum(line => line.LineTotal);
            order.TaxAmount = Math.Round(order.Lines.Sum(line => line.LineTotal * line.TaxPercent / 100m), 2);
            order.DiscountAmount = Math.Round(order.SubTotal * order.DiscountPercent / 100m, 2);
            order.DocTotal = Math.Round(order.SubTotal - order.DiscountAmount + order.TaxAmount, 2);
            order.UpdatedAt = updatedAt;
        }

        return (ordersUpdated, linesUpdated);
    }

    private static bool IsPostedToSap(SalesOrderEntity order)
        => order.IsSynced && order.SAPDocEntry.GetValueOrDefault() > 0;

    private BackfillWebOrderTaxResult EmptyResult(bool dryRun)
        => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, _configuredTaxPercent, dryRun);

    private static decimal NormalizeTaxPercent(decimal configuredVatRate)
        => configuredVatRate <= 1 ? configuredVatRate * 100m : configuredVatRate;
}
