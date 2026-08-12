using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSales;

/// <summary>
/// One route customer's trading history.
///
/// Reads the two tables a van sale can land in and presents them as one list. A sale is matched on the
/// route customer's id, or — for sales whose customer record was hard-deleted and re-created under the
/// same code — on the code together with the route it was sold on. The code alone is not enough: route
/// customer codes are only unique within a route, so two vans can both have a "TEST".
/// </summary>
public sealed class GetRouteCustomerSalesHandler(
    ApplicationDbContext db
) : IRequestHandler<GetRouteCustomerSalesQuery, ErrorOr<RouteCustomerSalesDetailDto>>
{
    public async Task<ErrorOr<RouteCustomerSalesDetailDto>> Handle(
        GetRouteCustomerSalesQuery query,
        CancellationToken cancellationToken)
    {
        var customer = await db.RouteCustomers
            .AsNoTracking()
            .Include(entity => entity.CreatedByUser)
            .FirstOrDefaultAsync(entity => entity.Id == query.RouteCustomerId, cancellationToken);

        if (customer is null)
        {
            return Errors.RouteCustomers.NotFound(query.RouteCustomerId);
        }

        var (from, to) = RouteCustomerSalesReporting.NormalizeWindow(query.From, query.To);
        var (fromUtc, toUtcExclusive) = RouteCustomerSalesReporting.ToUtcWindow(from, to);

        var sales = await ReadOfflineSalesAsync(customer, from, to, cancellationToken);
        sales.AddRange(await ReadOnlineSalesAsync(customer, fromUtc, toUtcExclusive, cancellationToken));
        sales = sales.OrderByDescending(sale => sale.SoldAt).ThenBy(sale => sale.Reference).ToList();

        var orders = await ReadOrdersAsync(customer, fromUtc, toUtcExclusive, cancellationToken);

        var lastSaleAt = sales.Count == 0 ? (DateTime?)null : sales.Max(sale => sale.SoldAt);

        return new RouteCustomerSalesDetailDto
        {
            Customer = MapCustomer(customer),
            From = from,
            To = to,
            SaleCount = sales.Count,
            LineCount = sales.Sum(sale => sale.Lines.Count),
            FirstSaleAt = sales.Count == 0 ? null : sales.Min(sale => sale.SoldAt),
            LastSaleAt = lastSaleAt,
            DaysSinceLastSale = RouteCustomerSalesReporting.DaysSinceLastSale(lastSaleAt),
            TotalsByCurrency = RouteCustomerSalesReporting.SumByCurrency(sales.Select(sale =>
                new RouteCustomerSalesTotalsDto
                {
                    Currency = sale.Currency,
                    SaleCount = 1,
                    Gross = sale.Total,
                    Vat = sale.VatAmount,
                    AmountPaid = sale.AmountPaid
                })),
            Sales = sales,
            Orders = orders,
            ProductMix = BuildProductMix(sales)
        };
    }

    private async Task<List<RouteCustomerSaleDto>> ReadOfflineSalesAsync(
        RouteCustomerEntity customer,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var rows = await db.DesktopSales
            .AsNoTracking()
            .Include(sale => sale.Lines)
            .Where(sale => sale.DocDate >= from && sale.DocDate <= to)
            .Where(sale => sale.RouteCustomerId == customer.Id ||
                (sale.RouteCustomerId == null &&
                 sale.RouteCustomerCode == customer.Code &&
                 sale.CardCode == customer.AssignedBusinessPartnerCode))
            .ToListAsync(cancellationToken);

        return rows.Select(sale => new RouteCustomerSaleDto
        {
            Source = RouteCustomerSaleSource.OfflineVanSale,
            Reference = sale.ExternalReferenceId,
            SoldAt = sale.DocDate,
            Currency = RouteCustomerSalesReporting.NormalizeCurrency(sale.Currency),
            Total = sale.TotalAmount,
            VatAmount = sale.VatAmount,
            AmountPaid = sale.AmountPaid,
            PaymentMethod = sale.PaymentMethod,
            PaymentReference = sale.PaymentReference,
            Status = RouteCustomerSalesReporting.DescribeOfflineSaleStatus(
                sale.ConsolidationStatus, sale.ReceiptIngestStatus, sale.SapDocNum),
            SapDocNum = sale.SapDocNum,
            SapDocEntry = sale.SapDocEntry,
            PostedAt = sale.PostedAt,
            ReceiptGlobalNo = sale.ReceiptGlobalNo,
            FiscalDayNo = sale.FiscalDayNo,
            FiscalVerificationCode = sale.FiscalVerificationCode,
            FiscalDeviceNumber = sale.FiscalDeviceNumber,
            WarehouseCode = sale.WarehouseCode,
            Lines = sale.Lines
                .OrderBy(line => line.LineNum)
                .Select(line => new RouteCustomerSaleLineDto
                {
                    LineNum = line.LineNum,
                    ItemCode = line.ItemCode,
                    ItemDescription = line.ItemDescription,
                    Quantity = line.Quantity,
                    UoMCode = line.UoMCode,
                    UnitPrice = line.UnitPrice,
                    LineTotal = line.LineTotal,
                    WarehouseCode = line.WarehouseCode
                })
                .ToList()
        }).ToList();
    }

    /// <summary>
    /// Only confirmed reservations. A pending one is stock being held for a sale that has not happened,
    /// and a cancelled or expired one is a sale that never did — counting either as revenue would invent
    /// takings the van never had.
    /// </summary>
    private async Task<List<RouteCustomerSaleDto>> ReadOnlineSalesAsync(
        RouteCustomerEntity customer,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken)
    {
        var rows = await db.StockReservations
            .AsNoTracking()
            .Include(reservation => reservation.Lines)
            .Where(reservation => reservation.Status == ReservationStatus.Confirmed)
            .Where(reservation => reservation.CreatedAt >= fromUtc && reservation.CreatedAt < toUtcExclusive)
            .Where(reservation => reservation.RouteCustomerId == customer.Id ||
                (reservation.RouteCustomerId == null &&
                 reservation.RouteCustomerCode == customer.Code &&
                 reservation.CardCode == customer.AssignedBusinessPartnerCode))
            .ToListAsync(cancellationToken);

        return rows.Select(reservation => new RouteCustomerSaleDto
        {
            Source = RouteCustomerSaleSource.OnlineInvoice,
            Reference = reservation.ExternalReferenceId,
            SoldAt = AuditService.ToCAT(reservation.CreatedAt),
            Currency = RouteCustomerSalesReporting.NormalizeCurrency(reservation.Currency),
            // A reservation records what was sold, not how it was taxed — the tax split lives on the SAP
            // invoice. Reporting zero VAT would be a claim; leaving it out is not.
            Total = reservation.TotalValue,
            VatAmount = 0m,
            AmountPaid = reservation.TotalValue,
            Status = reservation.SAPDocNum.HasValue ? $"Posted as {reservation.SAPDocNum}" : "Posted",
            SapDocNum = reservation.SAPDocNum,
            SapDocEntry = reservation.SAPDocEntry,
            PostedAt = reservation.ConfirmedAt,
            WarehouseCode = reservation.Lines.FirstOrDefault()?.WarehouseCode,
            Lines = reservation.Lines
                .OrderBy(line => line.LineNum)
                .Select(line => new RouteCustomerSaleLineDto
                {
                    LineNum = line.LineNum,
                    ItemCode = line.ItemCode,
                    ItemDescription = line.ItemDescription,
                    Quantity = line.ReservedQuantity,
                    UoMCode = line.UoMCode,
                    UnitPrice = line.UnitPrice,
                    LineTotal = line.LineTotal,
                    WarehouseCode = line.WarehouseCode
                })
                .ToList()
        }).ToList();
    }

    private async Task<List<RouteCustomerOrderDto>> ReadOrdersAsync(
        RouteCustomerEntity customer,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken)
    {
        var rows = await db.SalesOrders
            .AsNoTracking()
            .Where(order => order.OrderDate >= fromUtc && order.OrderDate < toUtcExclusive)
            .Where(order => order.RouteCustomerId == customer.Id ||
                (order.RouteCustomerId == null &&
                 order.RouteCustomerCode == customer.Code &&
                 order.CardCode == customer.AssignedBusinessPartnerCode))
            .OrderByDescending(order => order.OrderDate)
            .Select(order => new RouteCustomerOrderDto
            {
                Id = order.Id,
                OrderNumber = order.OrderNumber,
                OrderDate = order.OrderDate,
                Status = order.Status.ToString(),
                Currency = order.Currency,
                DocTotal = order.DocTotal,
                LineCount = order.Lines.Count,
                IsInvoiced = order.InvoiceId != null || order.SAPDocNum != null,
                SapDocNum = order.SAPDocNum
            })
            .ToListAsync(cancellationToken);

        foreach (var order in rows)
        {
            // A mobile order's lines are priced after capture, so a zero here is usually "not priced
            // yet" rather than a free order. Saying which is the difference between a report that reads
            // as broken and one that reads as waiting.
            order.IsAwaitingPricing = order.DocTotal == 0m && !order.IsInvoiced;
            order.OrderDate = AuditService.ToCAT(order.OrderDate);
        }

        return rows;
    }

    /// <summary>
    /// What this customer buys, from the sales already read. Grouped by item so the quantities add up:
    /// within one item code the unit of measure is consistent, across items it is not.
    /// </summary>
    private static List<RouteCustomerProductMixRowDto> BuildProductMix(List<RouteCustomerSaleDto> sales)
    {
        return sales
            .SelectMany(sale => sale.Lines.Select(line => new { sale, line }))
            .GroupBy(entry => entry.line.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RouteCustomerProductMixRowDto
            {
                ItemCode = group.Key,
                ItemDescription = group
                    .Select(entry => entry.line.ItemDescription)
                    .FirstOrDefault(description => !string.IsNullOrWhiteSpace(description)),
                Quantity = group.Sum(entry => entry.line.Quantity),
                UoMCode = group
                    .Select(entry => entry.line.UoMCode)
                    .FirstOrDefault(uom => !string.IsNullOrWhiteSpace(uom)),
                LineCount = group.Count(),
                CustomerCount = 1,
                LastSoldAt = group.Max(entry => entry.sale.SoldAt),
                TotalsByCurrency = RouteCustomerSalesReporting.SumByCurrency(group.Select(entry =>
                    new RouteCustomerSalesTotalsDto
                    {
                        Currency = entry.sale.Currency,
                        SaleCount = 1,
                        Gross = entry.line.LineTotal
                    }))
            })
            // Ranked by how often the item appears on a sale, not by value. Value would mean adding USD
            // to ZWG to get a sort key, and quantity would rank a case of bottles against a single
            // bottle. How regularly a shop reorders something is the one comparison that holds across
            // both, and it is also the one a route report is asking about.
            .OrderByDescending(row => row.LineCount)
            .ThenByDescending(row => row.Quantity)
            .ThenBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static RouteCustomerDto MapCustomer(RouteCustomerEntity customer) => new()
    {
        Id = customer.Id,
        AssignedBusinessPartnerCode = customer.AssignedBusinessPartnerCode,
        Code = customer.Code,
        Name = customer.Name,
        Phone = customer.Phone,
        Email = customer.Email,
        Address = customer.Address,
        VatNumber = customer.VatNumber,
        IsActive = customer.IsActive,
        CreatedByUserId = customer.CreatedByUserId,
        CreatedByUserName = customer.CreatedByUser?.Username,
        CreatedAt = customer.CreatedAt,
        UpdatedAt = customer.UpdatedAt
    };
}
