
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesOrderHistory;

public sealed class GetVanSalesOrderHistoryHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    ILogger<GetVanSalesOrderHistoryHandler> logger
) : IRequestHandler<GetVanSalesOrderHistoryQuery, ErrorOr<List<VanSalesLegacyOrderDto>>>
{
    public async Task<ErrorOr<List<VanSalesLegacyOrderDto>>> Handle(
        GetVanSalesOrderHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var normalizedType = query.Request.Type?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedType) &&
            !string.Equals(normalizedType, "SO", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedType, "INV", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidOrderType",
                "The van sales history endpoint supports only invoice and sales-order filters.");
        }

        var includeSalesOrders = string.IsNullOrWhiteSpace(normalizedType) ||
            string.Equals(normalizedType, "SO", StringComparison.OrdinalIgnoreCase);
        var includeInvoices = string.IsNullOrWhiteSpace(normalizedType) ||
            string.Equals(normalizedType, "INV", StringComparison.OrdinalIgnoreCase);

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var effectiveCustomerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
            db,
            user,
            logger,
            cancellationToken);

        var fromDateUtc = VanSalesCompatibilityMapper.ParseLegacyDate(query.Request.StartDate)?.Date;
        var toDateUtcExclusive = VanSalesCompatibilityMapper.ParseLegacyDate(query.Request.EndDate)?.Date.AddDays(1);

        var history = new List<VanSalesLegacyOrderDto>();

        if (includeSalesOrders)
        {
            history.AddRange(await GetSalesOrderHistoryAsync(
                user.Id,
                effectiveCustomerCodes,
                fromDateUtc,
                toDateUtcExclusive,
                cancellationToken));
        }

        if (includeInvoices)
        {
            history.AddRange(await GetInvoiceHistoryAsync(
                user.Id,
                effectiveCustomerCodes,
                fromDateUtc,
                toDateUtcExclusive,
                cancellationToken));
        }

        return history
            .OrderByDescending(order => VanSalesCompatibilityMapper.ParseLegacyDate(order.Timestamps.CreateDate) ?? DateTime.MinValue)
            .ThenByDescending(order => order.Id)
            .ToList();
    }

    private async Task<List<VanSalesLegacyOrderDto>> GetSalesOrderHistoryAsync(
        Guid userId,
        IReadOnlyCollection<string> effectiveCustomerCodes,
        DateTime? fromDateUtc,
        DateTime? toDateUtcExclusive,
        CancellationToken cancellationToken)
    {
        var salesOrdersQuery = db.SalesOrders
            .AsNoTracking()
            .Where(order => order.Source == SalesOrderSource.Mobile && order.CreatedByUserId == userId);

        if (effectiveCustomerCodes.Count > 0)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => effectiveCustomerCodes.Contains(order.CardCode));
        }

        if (fromDateUtc.HasValue)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => order.OrderDate >= fromDateUtc.Value);
        }

        if (toDateUtcExclusive.HasValue)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => order.OrderDate < toDateUtcExclusive.Value);
        }

        var orders = await salesOrdersQuery
            .OrderByDescending(order => order.OrderDate)
            .ThenByDescending(order => order.Id)
            .Select(order => new SalesOrderDto
            {
                Id = order.Id,
                SAPDocEntry = order.SAPDocEntry,
                SAPDocNum = order.SAPDocNum,
                OrderNumber = order.OrderNumber,
                OrderDate = order.OrderDate,
                DeliveryDate = order.DeliveryDate,
                CardCode = order.CardCode,
                CardName = order.CardName,
                Currency = order.Currency,
                TaxAmount = order.TaxAmount,
                DocTotal = order.DocTotal,
                CreatedAt = order.CreatedAt,
                ApprovedDate = order.ApprovedDate,
                InvoiceSapDocNum = order.Invoice != null ? order.Invoice.SAPDocNum : null,
                Status = order.Status,
                Lines = order.Lines
                    .OrderBy(line => line.LineNum)
                    .Select(line => new SalesOrderLineDto
                    {
                        Id = line.Id,
                        LineNum = line.LineNum,
                        ItemCode = line.ItemCode,
                        ItemDescription = line.ItemDescription,
                        Quantity = line.Quantity,
                        UnitPrice = line.UnitPrice,
                        LineTotal = line.LineTotal
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return orders
            .Select(VanSalesCompatibilityMapper.MapLegacySalesOrder)
            .ToList();
    }

    private async Task<List<VanSalesLegacyOrderDto>> GetInvoiceHistoryAsync(
        Guid userId,
        IReadOnlyCollection<string> effectiveCustomerCodes,
        DateTime? fromDateUtc,
        DateTime? toDateUtcExclusive,
        CancellationToken cancellationToken)
    {
        var userIdValue = userId.ToString();
        var fiscalTransactions = await db.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction =>
                transaction.DocumentType == "Invoice" &&
                transaction.DocNum > 0 &&
                transaction.CreatedByUserId == userIdValue)
            .Where(transaction => !fromDateUtc.HasValue || transaction.TimestampUtc >= fromDateUtc.Value)
            .Where(transaction => !toDateUtcExclusive.HasValue || transaction.TimestampUtc < toDateUtcExclusive.Value)
            .OrderByDescending(transaction => transaction.TimestampUtc)
            .ToListAsync(cancellationToken);

        if (fiscalTransactions.Count == 0)
        {
            return new List<VanSalesLegacyOrderDto>();
        }

        var latestFiscalByDocNum = fiscalTransactions
            .GroupBy(transaction => transaction.DocNum)
            .ToDictionary(group => group.Key, group => group.First());

        var sapFromDate = fromDateUtc ?? fiscalTransactions.Min(transaction => transaction.TimestampUtc).Date;
        var sapToDate = toDateUtcExclusive.HasValue
            ? toDateUtcExclusive.Value.AddDays(-1)
            : fiscalTransactions.Max(transaction => transaction.TimestampUtc).Date;

        var invoices = await sapClient.GetInvoiceHeadersByDateRangeAsync(
            sapFromDate,
            sapToDate,
            null,
            includeDocumentLines: true,
            cancellationToken);

        return invoices
            .Where(invoice => latestFiscalByDocNum.ContainsKey(invoice.DocNum))
            .Where(invoice => effectiveCustomerCodes.Count == 0 ||
                effectiveCustomerCodes.Any(code => string.Equals(code, invoice.CardCode, StringComparison.OrdinalIgnoreCase)))
            .Select(invoice => VanSalesCompatibilityMapper.MapLegacyInvoice(invoice, latestFiscalByDocNum[invoice.DocNum]))
            .OrderByDescending(order => VanSalesCompatibilityMapper.ParseLegacyDate(order.Timestamps.CreateDate) ?? DateTime.MinValue)
            .ThenByDescending(order => order.Id)
            .ToList();
    }
}