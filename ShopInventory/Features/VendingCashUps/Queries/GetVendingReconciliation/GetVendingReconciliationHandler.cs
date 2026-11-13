using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;

namespace ShopInventory.Features.VendingCashUps.Queries.GetVendingReconciliation;

/// <summary>
/// Lays every vending cashier-day in the range against its count.
/// </summary>
/// <remarks>
/// <para>
/// A day with sales and no count is a row of its own, "not cashed up", with no counted figure and no
/// variance — a missing count must never read as a shortfall of the whole day's takings.
/// </para>
/// <para>
/// Totals are per currency. The counted ones (<c>Accounted</c>, <c>Banked</c>, the variances) are over
/// counted days only, and <c>InvoicedOnCountedDays</c> is the invoiced figure over the same days, so the
/// two can be compared without the uncounted days making the count look short.
/// </para>
/// </remarks>
public sealed class GetVendingReconciliationHandler(ApplicationDbContext db)
    : IRequestHandler<GetVendingReconciliationQuery, ErrorOr<VendingReconciliationDto>>
{
    public async Task<ErrorOr<VendingReconciliationDto>> Handle(
        GetVendingReconciliationQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;
        var warehouse = string.IsNullOrWhiteSpace(query.WarehouseCode)
            ? null
            : VendingCashUpSheets.NormalizeWarehouse(query.WarehouseCode);

        var sales = await VendingCashUpExpectations.LoadAsync(db, from, to, null, warehouse, cancellationToken);

        var cashUpQuery = db.VendingCashUps
            .AsNoTracking()
            .Include(cashUp => cashUp.Currencies)
            .Include(cashUp => cashUp.Tenders)
            .Where(cashUp => cashUp.BusinessDate >= from && cashUp.BusinessDate <= to);
        if (warehouse is not null)
        {
            cashUpQuery = cashUpQuery.Where(cashUp => cashUp.WarehouseCode == warehouse);
        }

        var cashUps = await cashUpQuery.AsSplitQuery().ToListAsync(cancellationToken);

        var salesByDay = sales
            .GroupBy(row => (row.CashierUserId, row.WarehouseCode, row.BusinessDate))
            .ToDictionary(group => group.Key, group => group.ToList());
        var cashUpByDay = cashUps.ToDictionary(cashUp => (cashUp.CashierUserId, cashUp.WarehouseCode, cashUp.BusinessDate));

        var days = salesByDay.Keys.Union(cashUpByDay.Keys).ToList();
        var directory = await VendingCashUpDirectory.LoadAsync(
            db, days.Select(day => day.CashierUserId).ToList(), cancellationToken);

        var rows = new List<VendingReconciliationRowDto>();
        foreach (var day in days)
        {
            var daySales = salesByDay.TryGetValue(day, out var found) ? found : [];
            cashUpByDay.TryGetValue(day, out var cashUp);
            rows.Add(Row(day.CashierUserId, day.WarehouseCode, day.BusinessDate, cashUp, daySales, directory));
        }

        rows = rows
            .OrderByDescending(row => row.BusinessDate)
            .ThenBy(row => row.DepotName ?? row.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.CashierName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new VendingReconciliationDto(from, to, rows, Totals(rows), Counts(rows, cashUps));
    }

    public static VendingReconciliationRowDto Row(
        Guid cashierUserId,
        string warehouseCode,
        DateTime businessDate,
        Models.Entities.VendingCashUpEntity? cashUp,
        IReadOnlyCollection<VendingCashierDaySales> sales,
        VendingCashUpDirectory directory)
    {
        var reading = VendingCashUpReading.Read(cashUp, sales, []);

        var currencies = reading.Currencies
            .Select(currency =>
            {
                var tenders = reading.Tenders.Where(tender => tender.Currency == currency.Currency).ToList();
                var invoiced = VendingCashUpMath.Round(currency.ExpectedCash + tenders.Sum(tender => tender.Expected));
                var count = currency.ExpectedCashSales + tenders.Sum(tender => tender.ExpectedSales);

                if (!reading.Counted)
                {
                    return new VendingReconciliationCurrencyDto(
                        currency.Currency, invoiced, count, currency.ExpectedCash, null, null, null, null, null);
                }

                var accounted = VendingCashUpMath.Round(
                    currency.CountedCash!.Value - currency.OpeningFloat!.Value + tenders.Sum(tender => tender.Declared ?? 0m));

                return new VendingReconciliationCurrencyDto(
                    currency.Currency,
                    invoiced,
                    count,
                    currency.ExpectedCash,
                    currency.OpeningFloat,
                    currency.CountedCash,
                    accounted,
                    currency.Banked,
                    VendingCashUpMath.Round(accounted - invoiced));
            })
            // A currency the depot neither sold nor counted in says nothing.
            .Where(currency => currency.Invoiced != 0m || (currency.CountedCash ?? 0m) != 0m || (currency.Banked ?? 0m) != 0m
                || (currency.OpeningFloat ?? 0m) != 0m || (currency.Accounted ?? 0m) != 0m)
            .ToList();

        var partner = cashUp?.BusinessPartnerCode ?? directory.PartnerCode(warehouseCode, cashierUserId);

        return new VendingReconciliationRowDto(
            cashUp?.Id,
            cashUp?.Revision ?? 0,
            cashUp?.Status ?? VendingCashUpReconciliationStatus.NotCashedUp,
            businessDate,
            cashierUserId,
            directory.Username(cashierUserId, cashUp?.CashierUsername),
            directory.Name(cashierUserId, cashUp?.CashierUsername),
            partner,
            directory.PartnerName(partner),
            warehouseCode,
            currencies,
            reading.Balanced,
            reading.NeedsReason,
            reading.SalesChangedSinceCount,
            cashUp?.VarianceReason,
            cashUp?.QueryNote,
            cashUp?.CapturedByName,
            VendingCashUpSheets.Cat(cashUp?.CapturedAt),
            cashUp?.ApprovedByName,
            VendingCashUpSheets.Cat(cashUp?.ApprovedAt));
    }

    public static List<VendingReconciliationCurrencyTotalDto> Totals(IReadOnlyCollection<VendingReconciliationRowDto> rows) =>
        rows
            .SelectMany(row => row.Currencies.Select(currency => (Row: row, Currency: currency)))
            .GroupBy(pair => pair.Currency.Currency)
            .OrderBy(group => VendingCashUpReading.CurrencyOrder(group.Key))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var counted = group.Where(pair => pair.Currency.Variance is not null).ToList();
                return new VendingReconciliationCurrencyTotalDto(
                    group.Key,
                    VendingCashUpMath.Round(group.Sum(pair => pair.Currency.Invoiced)),
                    VendingCashUpMath.Round(counted.Sum(pair => pair.Currency.Invoiced)),
                    VendingCashUpMath.Round(counted.Sum(pair => pair.Currency.Accounted ?? 0m)),
                    VendingCashUpMath.Round(counted.Sum(pair => pair.Currency.Banked ?? 0m)),
                    VendingCashUpMath.Round(counted.Sum(pair => pair.Currency.Variance ?? 0m)),
                    VendingCashUpMath.Round(-counted
                        .Where(pair => pair.Row.Status != VendingCashUpStatuses.Approved && pair.Currency.Variance < 0m)
                        .Sum(pair => pair.Currency.Variance!.Value)));
            })
            .ToList();

    public static VendingReconciliationCountsDto Counts(
        IReadOnlyCollection<VendingReconciliationRowDto> rows,
        IReadOnlyCollection<Models.Entities.VendingCashUpEntity> cashUps) =>
        new(
            rows.Count,
            rows.Count(row => row.Status == VendingCashUpReconciliationStatus.NotCashedUp),
            rows.Count(row => row.Status == VendingCashUpStatuses.Submitted),
            rows.Count(row => row.Status == VendingCashUpStatuses.Queried),
            rows.Count(row => row.Status == VendingCashUpStatuses.Approved),
            cashUps.Count(cashUp => cashUp.BalancedOnFirstCount),
            rows.Count(row => row.NeedsReason));
}
