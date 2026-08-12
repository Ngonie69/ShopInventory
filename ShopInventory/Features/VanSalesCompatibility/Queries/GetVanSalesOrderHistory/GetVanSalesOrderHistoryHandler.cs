
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

        var window = VanSalesLegacyDateWindow.Parse(query.Request.StartDate, query.Request.EndDate);

        var history = new List<VanSalesLegacyOrderDto>();

        if (includeSalesOrders)
        {
            history.AddRange(await GetSalesOrderHistoryAsync(
                user.Id,
                effectiveCustomerCodes,
                window,
                cancellationToken));
        }

        if (includeInvoices)
        {
            history.AddRange(await GetInvoiceHistoryAsync(
                user.Id,
                effectiveCustomerCodes,
                window,
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
        VanSalesLegacyDateWindow window,
        CancellationToken cancellationToken)
    {
        var salesOrdersQuery = db.SalesOrders
            .AsNoTracking()
            .Where(order => order.Source == SalesOrderSource.Mobile && order.CreatedByUserId == userId);

        if (effectiveCustomerCodes.Count > 0)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => effectiveCustomerCodes.Contains(order.CardCode));
        }

        // OrderDate is timestamptz, so the trading days the handset asked for are compared as the UTC
        // instants they cover rather than as bare dates.
        if (window.FromUtc is { } fromUtc)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => order.OrderDate >= fromUtc);
        }

        if (window.ToUtcExclusive is { } toUtcExclusive)
        {
            salesOrdersQuery = salesOrdersQuery.Where(order => order.OrderDate < toUtcExclusive);
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
        VanSalesLegacyDateWindow window,
        CancellationToken cancellationToken)
    {
        // TimestampUtc is timestamptz, so it takes the UTC instants the trading days cover.
        var fromUtc = window.FromUtc;
        var toUtcExclusive = window.ToUtcExclusive;

        var userIdValue = userId.ToString();
        var fiscalTransactions = await db.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction =>
                transaction.DocumentType == "Invoice" &&
                transaction.DocNum > 0 &&
                transaction.CreatedByUserId == userIdValue)
            .Where(transaction => !fromUtc.HasValue || transaction.TimestampUtc >= fromUtc.Value)
            .Where(transaction => !toUtcExclusive.HasValue || transaction.TimestampUtc < toUtcExclusive.Value)
            .OrderByDescending(transaction => transaction.TimestampUtc)
            .ToListAsync(cancellationToken);

        if (fiscalTransactions.Count == 0)
        {
            return new List<VanSalesLegacyOrderDto>();
        }

        var latestFiscalByDocNum = fiscalTransactions
            .GroupBy(transaction => transaction.DocNum)
            .ToDictionary(group => group.Key, group => group.First());

        // SAP filters DocDate as a calendar date in its own CAT terms, so this half takes the trading
        // days themselves. An unasked-for bound falls back to the days the fiscal receipts landed on,
        // which are instants and have to be read in CAT to name the right day.
        var sapFromDate = window.FromDate
            ?? AuditService.ToCAT(fiscalTransactions.Min(transaction => transaction.TimestampUtc)).Date;
        var sapToDate = window.ToDate
            ?? AuditService.ToCAT(fiscalTransactions.Max(transaction => transaction.TimestampUtc)).Date;

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