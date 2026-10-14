using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sales;
using ShopInventory.Common.Stock;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetCreditReturns;

/// <summary>
/// Reads the stock that credit notes returned to a warehouse's ledger.
/// </summary>
/// <remarks>
/// <para>
/// A credit puts its units back on the ledger as soon as ZIMRA has it, so the shop can sell them
/// again that afternoon. The local stock route shows the result — a higher figure — and nothing
/// about why: on a till's stock pages the units arrived with no sale and no transfer behind them,
/// and were listed as a correction somebody had to explain. This is the document behind them.
/// </para>
/// <para>
/// Read from the movement journal rather than from the credits themselves, because the journal is
/// what moved the figure. A credit raised as fiscalise-only returns nothing, and one whose item had
/// no row in the day's count could not be put back; neither has a movement, and neither is listed.
/// </para>
/// <para>
/// Read from this API's own database; SAP is not asked.
/// </para>
/// </remarks>
public sealed class GetCreditReturnsHandler(ApplicationDbContext context)
    : IRequestHandler<GetCreditReturnsQuery, ErrorOr<List<CreditReturnDto>>>
{
    /// <summary>A quarter, the same reach as the transfer adjustments beside it.</summary>
    public const int MaxDays = 92;

    public async Task<ErrorOr<List<CreditReturnDto>>> Handle(
        GetCreditReturnsQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;

        if (from > to)
            return Errors.DesktopIntegration.ValidationFailed("fromDate must be before or equal to toDate");

        if ((to - from).TotalDays + 1 > MaxDays)
            return Errors.DesktopIntegration.ValidationFailed($"A range can cover at most {MaxDays} days");

        var movements = await context.StockMovements
            .AsNoTracking()
            .Where(movement => movement.Kind == StockMovementKinds.Release
                               && movement.WarehouseCode == query.WarehouseCode
                               && movement.LedgerDay >= from
                               && movement.LedgerDay <= to
                               && movement.DocumentKey != null
                               && movement.DocumentKey.StartsWith(DesktopCreditStockReturn.KeyPrefix))
            .OrderBy(movement => movement.OccurredAt)
            .ThenBy(movement => movement.Id)
            .Select(movement => new
            {
                movement.LedgerDay,
                movement.OccurredAt,
                movement.ItemCode,
                movement.WarehouseCode,
                movement.Quantity,
                movement.DocumentKey
            })
            .ToListAsync(cancellationToken);

        if (movements.Count == 0)
            return new List<CreditReturnDto>();

        var storedNumbers = movements
            .Select(movement => DesktopCreditStockReturn.CreditNumberOf(movement.DocumentKey)!)
            .Distinct()
            .ToList();

        var credits = await context.DesktopCreditNotes
            .AsNoTracking()
            .Where(credit => storedNumbers.Contains(credit.Number))
            .Select(credit => new
            {
                credit.Id,
                credit.Number,
                credit.SaleId,
                credit.Reason,
                SaleReference = credit.Sale.ExternalReferenceId,
                SaleDate = credit.Sale.DocDate
            })
            .ToListAsync(cancellationToken);

        var saleIds = credits.Select(credit => credit.SaleId).Distinct().ToList();
        var quoted = await DesktopCreditNoteNumber.ForSalesAsync(context, saleIds, cancellationToken);

        var names = (await context.DesktopSaleLines
                .AsNoTracking()
                .Where(line => saleIds.Contains(line.SaleId))
                .Select(line => new { line.SaleId, line.ItemCode, line.ItemDescription })
                .ToListAsync(cancellationToken))
            .Where(line => !string.IsNullOrWhiteSpace(line.ItemDescription))
            .GroupBy(line => (line.SaleId, line.ItemCode))
            .ToDictionary(line => line.Key, line => line.First().ItemDescription);

        var creditByNumber = credits.ToDictionary(credit => credit.Number, StringComparer.Ordinal);

        return movements
            .Select(movement =>
            {
                var stored = DesktopCreditStockReturn.CreditNumberOf(movement.DocumentKey)!;
                var credit = creditByNumber.GetValueOrDefault(stored);

                return new CreditReturnDto(
                    movement.LedgerDay,
                    // Written from DateTime.UtcNow and read back from a column that keeps no kind.
                    DateTime.SpecifyKind(movement.OccurredAt, DateTimeKind.Utc),
                    movement.ItemCode,
                    credit is null ? null : names.GetValueOrDefault((credit.SaleId, movement.ItemCode)),
                    movement.WarehouseCode,
                    movement.Quantity,
                    // A journal row whose credit is gone still moved the stock, so it is still listed,
                    // under the only number left for it.
                    credit is null ? stored : quoted.GetValueOrDefault(credit.Id, stored),
                    credit is null ? null : DesktopSaleNumber.Format(credit.SaleId),
                    credit?.SaleReference,
                    credit?.SaleDate,
                    credit?.Reason);
            })
            .ToList();
    }
}
