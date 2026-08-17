using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries;

/// <summary>
/// Reads van sales out of the two tables they land in and hands back one normalised stream.
///
/// This is the single place the four recurring problems are solved, so no report has to solve them
/// again and no two reports can answer them differently:
///
/// 1. <b>The union.</b> An offline sale becomes a <c>DesktopSale</c>; an online one posts straight to
///    SAP and leaves only a confirmed <c>StockReservation</c>. Reading either alone under-reports,
///    and does so silently — there is no error and no empty result to notice.
/// 2. <b>The two clocks.</b> <c>DesktopSale.DocDate</c> is already a bare CAT trading day, but
///    <c>StockReservation.CreatedAt</c> is a UTC instant. The two halves need different treatment to
///    land on the same day; ignoring that puts every sale after 22:00 CAT on the day before.
/// 3. <b>The attribution.</b> The rep is a user id held as a string in <c>CreatedBy</c>.
/// 4. <b>The shop.</b> The account on the document is the van's own, so the buyer is only ever in the
///    route customer columns — and is null on history captured before handsets started sending it.
///
/// Static, taking the context as an argument, for the same reason
/// <c>RouteCustomerSalesReporting</c> is: there is no state to hold and nothing to register.
///
/// Both reads are local — Postgres only, no SAP — so a wide window is cheap and needs no deadline.
/// </summary>
public static class VanSalesFactReader
{
    /// <summary>
    /// Every van sale in the period, at document grain. This is what the compliance report counts.
    /// </summary>
    public static async Task<List<VanSaleFact>> LoadSalesAsync(
        ApplicationDbContext db,
        VanSalesFactFilter filter,
        CancellationToken cancellationToken)
    {
        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(filter.From, filter.To);

        var offline = await db.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SourceSystem == SaleSourceSystems.VanSales
                           && sale.DocDate >= filter.From
                           && sale.DocDate <= filter.To
                           && sale.CreatedBy != null)
            .Select(sale => new
            {
                sale.CreatedBy,
                sale.DocDate,
                sale.ExternalReferenceId,
                sale.RouteCustomerCode,
                sale.RouteCustomerName,
                sale.PaymentMethod,
                sale.TotalAmount,
                sale.Currency,
                sale.WarehouseCode
            })
            .ToListAsync(cancellationToken);

        // Pending, cancelled and expired reservations are not sales. Only a confirmed one means an
        // invoice was posted.
        var online = await db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservation.SourceSystem == SaleSourceSystems.VanSales
                                  && reservation.Status == ReservationStatus.Confirmed
                                  && reservation.CreatedAt >= windowStartUtc
                                  && reservation.CreatedAt < windowEndUtc
                                  && reservation.CreatedBy != null)
            .Select(reservation => new
            {
                reservation.CreatedBy,
                reservation.CreatedAt,
                reservation.ExternalReferenceId,
                reservation.RouteCustomerCode,
                reservation.RouteCustomerName,
                reservation.PaymentMethod,
                reservation.TotalValue,
                reservation.Currency
            })
            .ToListAsync(cancellationToken);

        var facts = new List<VanSaleFact>(offline.Count + online.Count);

        foreach (var sale in offline)
        {
            if (!TryAttribute(sale.CreatedBy, filter, out var userId))
            {
                continue;
            }

            facts.Add(new VanSaleFact(
                UserId: userId,
                TradingDate: sale.DocDate.Date,
                Source: VanSaleSource.OfflineBatch,
                ExternalReferenceId: sale.ExternalReferenceId,
                RouteCustomerCode: Trimmed(sale.RouteCustomerCode),
                RouteCustomerName: Trimmed(sale.RouteCustomerName),
                PaymentMethod: sale.PaymentMethod,
                TotalAmount: sale.TotalAmount,
                Currency: sale.Currency,
                WarehouseCode: sale.WarehouseCode));
        }

        foreach (var reservation in online)
        {
            if (!TryAttribute(reservation.CreatedBy, filter, out var userId))
            {
                continue;
            }

            facts.Add(new VanSaleFact(
                UserId: userId,
                TradingDate: VanSalesFacts.TradingDayOf(reservation.CreatedAt),
                Source: VanSaleSource.OnlineInvoice,
                ExternalReferenceId: reservation.ExternalReferenceId,
                RouteCustomerCode: Trimmed(reservation.RouteCustomerCode),
                RouteCustomerName: Trimmed(reservation.RouteCustomerName),
                // Null on every sale a handset made before the payment picker shipped. Left as null so
                // it reports as unallocated; defaulting it to cash would hide a declaration variance.
                PaymentMethod: reservation.PaymentMethod,
                TotalAmount: reservation.TotalValue,
                Currency: reservation.Currency,
                // A reservation carries its warehouse on the line, not the header.
                WarehouseCode: null));
        }

        return Order(facts, fact => fact.TradingDate, fact => fact.ExternalReferenceId);
    }

    /// <summary>
    /// Every van sale line in the period. This is the grain SKU, discount and volume reporting needs,
    /// and nothing reads it today.
    ///
    /// The quantity columns do not mean the same thing in the two tables — see
    /// <see cref="VanSaleLineFact"/> for which is which and why both are carried.
    /// </summary>
    public static async Task<List<VanSaleLineFact>> LoadSaleLinesAsync(
        ApplicationDbContext db,
        VanSalesFactFilter filter,
        CancellationToken cancellationToken)
    {
        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(filter.From, filter.To);

        var offline = await db.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SourceSystem == SaleSourceSystems.VanSales
                           && sale.DocDate >= filter.From
                           && sale.DocDate <= filter.To
                           && sale.CreatedBy != null)
            .Select(sale => new
            {
                sale.CreatedBy,
                sale.DocDate,
                sale.ExternalReferenceId,
                sale.RouteCustomerCode,
                sale.RouteCustomerName,
                sale.PaymentMethod,
                sale.Currency,
                Lines = sale.Lines
                    .Select(line => new
                    {
                        line.LineNum,
                        line.ItemCode,
                        line.ItemDescription,
                        line.Quantity,
                        line.UoMCode,
                        line.UnitPrice,
                        line.LineTotal,
                        line.DiscountPercent,
                        line.WarehouseCode
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        var online = await db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservation.SourceSystem == SaleSourceSystems.VanSales
                                  && reservation.Status == ReservationStatus.Confirmed
                                  && reservation.CreatedAt >= windowStartUtc
                                  && reservation.CreatedAt < windowEndUtc
                                  && reservation.CreatedBy != null)
            .Select(reservation => new
            {
                reservation.CreatedBy,
                reservation.CreatedAt,
                reservation.ExternalReferenceId,
                reservation.RouteCustomerCode,
                reservation.RouteCustomerName,
                reservation.PaymentMethod,
                reservation.Currency,
                Lines = reservation.Lines
                    .Select(line => new
                    {
                        line.LineNum,
                        line.ItemCode,
                        line.ItemDescription,
                        line.OriginalQuantity,
                        line.ReservedQuantity,
                        line.UoMCode,
                        line.UnitPrice,
                        line.LineTotal,
                        line.DiscountPercent,
                        line.WarehouseCode
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        var facts = new List<VanSaleLineFact>();

        foreach (var sale in offline)
        {
            if (!TryAttribute(sale.CreatedBy, filter, out var userId))
            {
                continue;
            }

            foreach (var line in sale.Lines)
            {
                facts.Add(new VanSaleLineFact(
                    UserId: userId,
                    TradingDate: sale.DocDate.Date,
                    Source: VanSaleSource.OfflineBatch,
                    ExternalReferenceId: sale.ExternalReferenceId,
                    RouteCustomerCode: Trimmed(sale.RouteCustomerCode),
                    RouteCustomerName: Trimmed(sale.RouteCustomerName),
                    LineNum: line.LineNum,
                    ItemCode: line.ItemCode,
                    ItemDescription: line.ItemDescription,
                    Quantity: line.Quantity,
                    // The offline path records one quantity only, so the inventory-unit figure is not
                    // unknown-because-zero, it is simply not recorded.
                    InventoryQuantity: null,
                    UoMCode: line.UoMCode,
                    UnitPrice: line.UnitPrice,
                    LineTotal: line.LineTotal,
                    DiscountPercent: line.DiscountPercent,
                    WarehouseCode: line.WarehouseCode,
                    Currency: sale.Currency,
                    PaymentMethod: sale.PaymentMethod));
            }
        }

        foreach (var reservation in online)
        {
            if (!TryAttribute(reservation.CreatedBy, filter, out var userId))
            {
                continue;
            }

            var tradingDate = VanSalesFacts.TradingDayOf(reservation.CreatedAt);

            foreach (var line in reservation.Lines)
            {
                facts.Add(new VanSaleLineFact(
                    UserId: userId,
                    TradingDate: tradingDate,
                    Source: VanSaleSource.OnlineInvoice,
                    ExternalReferenceId: reservation.ExternalReferenceId,
                    RouteCustomerCode: Trimmed(reservation.RouteCustomerCode),
                    RouteCustomerName: Trimmed(reservation.RouteCustomerName),
                    LineNum: line.LineNum,
                    ItemCode: line.ItemCode,
                    ItemDescription: line.ItemDescription,
                    // OriginalQuantity is what the rep asked for, in the unit UoMCode names, which is
                    // the pair that matches UnitPrice. ReservedQuantity is the same line converted to
                    // the inventory unit and belongs in the other column.
                    Quantity: line.OriginalQuantity,
                    InventoryQuantity: line.ReservedQuantity,
                    UoMCode: line.UoMCode,
                    UnitPrice: line.UnitPrice,
                    LineTotal: line.LineTotal,
                    DiscountPercent: line.DiscountPercent,
                    WarehouseCode: line.WarehouseCode,
                    Currency: reservation.Currency,
                    PaymentMethod: reservation.PaymentMethod));
            }
        }

        return facts
            .OrderBy(fact => fact.TradingDate)
            .ThenBy(fact => fact.ExternalReferenceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(fact => fact.LineNum)
            .ToList();
    }

    /// <summary>
    /// Resolves the rep and applies the one-rep filter.
    ///
    /// The filter is applied here rather than in SQL because <c>CreatedBy</c> is a string column: a
    /// comparison against a formatted Guid would match nothing at all if any writer ever used a
    /// different format, and would do so without erroring.
    /// </summary>
    private static bool TryAttribute(string? createdBy, VanSalesFactFilter filter, out Guid userId)
    {
        if (!VanSalesFacts.TryResolveRep(createdBy, out userId))
        {
            return false;
        }

        return !filter.UserId.HasValue || userId == filter.UserId.Value;
    }

    /// <summary>
    /// Empty and whitespace both mean "no shop recorded", and only one of them survives a null check.
    /// </summary>
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<T> Order<T>(
        List<T> facts,
        Func<T, DateTime> date,
        Func<T, string> reference) =>
        facts
            .OrderBy(date)
            .ThenBy(reference, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
