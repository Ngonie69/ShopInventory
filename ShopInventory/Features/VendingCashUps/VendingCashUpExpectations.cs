using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;

namespace ShopInventory.Features.VendingCashUps;

/// <summary>What one cashier's vending sales came to on one day at one warehouse, in one currency and tender.</summary>
public sealed record VendingCashierDaySales(
    Guid CashierUserId,
    string WarehouseCode,
    DateTime BusinessDate,
    string Currency,
    string Tender,
    decimal Amount,
    int Sales);

/// <summary>
/// Reads the expected side of a cash-up from the vending sales.
/// </summary>
/// <remarks>
/// <para>
/// A sale belongs to the account that rang it up (<c>CreatedBy</c>, the till's signed-in user), the
/// warehouse it drew on, and its <c>DocDate</c> — the same day the Vending sales page files it under, so
/// a cash-up's expected figure is the sum of the invoices that page lists for that cashier and day.
/// </para>
/// <para>
/// The amount is <c>TotalAmount</c>, the invoice gross including VAT, because that is what the customer
/// handed over. A vending line total is before VAT (see <c>CreateDesktopSaleHandler</c>), so summing
/// lines here would make every honest drawer look short by exactly the VAT.
/// </para>
/// <para>
/// Credit notes are not taken off. A vending credit note is raised in the portal against a sale, not
/// paid out of the drawer, so nothing says the cashier handed cash back.
/// </para>
/// </remarks>
public static class VendingCashUpExpectations
{
    public static async Task<List<VendingCashierDaySales>> LoadAsync(
        ApplicationDbContext context,
        DateTime fromDate,
        DateTime toDate,
        Guid? cashierUserId,
        string? warehouseCode,
        CancellationToken cancellationToken)
    {
        var from = fromDate.Date;
        var toExclusive = toDate.Date.AddDays(1);

        var query = context.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SourceSystem == SaleSourceSystems.Vending
                && sale.DocDate >= from
                && sale.DocDate < toExclusive
                && sale.CreatedBy != null);

        if (cashierUserId is { } cashier)
        {
            var createdBy = cashier.ToString();
            query = query.Where(sale => sale.CreatedBy == createdBy);
        }

        if (!string.IsNullOrWhiteSpace(warehouseCode))
        {
            var warehouse = warehouseCode.Trim();
            query = query.Where(sale => sale.WarehouseCode == warehouse);
        }

        var grouped = await query
            .GroupBy(sale => new
            {
                sale.CreatedBy,
                sale.WarehouseCode,
                sale.DocDate,
                sale.Currency,
                sale.PaymentMethod
            })
            .Select(group => new
            {
                group.Key.CreatedBy,
                group.Key.WarehouseCode,
                group.Key.DocDate,
                group.Key.Currency,
                group.Key.PaymentMethod,
                Amount = group.Sum(sale => sale.TotalAmount),
                Sales = group.Count()
            })
            .ToListAsync(cancellationToken);

        // Normalised here rather than in SQL: the casing of a tender and the spelling of ZiG vary by
        // till build, and each variant has to fold onto one line before it is compared with a count.
        return grouped
            .Where(row => Guid.TryParse(row.CreatedBy, out _))
            .GroupBy(row => new
            {
                Cashier = Guid.Parse(row.CreatedBy!),
                Warehouse = row.WarehouseCode.Trim().ToUpperInvariant(),
                Date = row.DocDate.Date,
                Currency = VendingCashUpMath.NormalizeCurrency(row.Currency),
                Tender = VendingCashUpMath.ReconciledTender(row.PaymentMethod)
            })
            .Select(group => new VendingCashierDaySales(
                group.Key.Cashier,
                group.Key.Warehouse,
                group.Key.Date,
                group.Key.Currency,
                group.Key.Tender,
                VendingCashUpMath.Round(group.Sum(row => row.Amount)),
                group.Sum(row => row.Sales)))
            .ToList();
    }
}
