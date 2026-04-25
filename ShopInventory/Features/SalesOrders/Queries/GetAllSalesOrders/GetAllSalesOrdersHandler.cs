using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.SalesOrders.Queries.GetAllSalesOrders;

public sealed class GetAllSalesOrdersHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    ILogger<GetAllSalesOrdersHandler> logger
) : IRequestHandler<GetAllSalesOrdersQuery, ErrorOr<SalesOrderListResponseDto>>
{
    public async Task<ErrorOr<SalesOrderListResponseDto>> Handle(
        GetAllSalesOrdersQuery request,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, 10000);
        var customerSearch = NormalizeSearchValue(request.CardCode);
        var orderSearch = NormalizeSearchValue(request.Search);
        var localFromDate = NormalizeUtcDate(request.FromDate);
        var sapToDate = NormalizeUtcDate(request.ToDate);
        var localToExclusive = sapToDate?.AddDays(1);

        if (request.Source.HasValue)
        {
            return await GetAllFromLocalAsync(
                page,
                pageSize,
                request.Status,
                customerSearch,
                localFromDate,
                localToExclusive,
                request.Source,
                orderSearch,
                cancellationToken);
        }

        var localOffset = Math.Max(0, (page - 1) * pageSize);
        var localUnsyncedCount = await BuildLocalOrdersQuery(
                unsyncedOnly: true,
                request.Status,
                customerSearch,
                localFromDate,
                localToExclusive,
                source: null,
                orderSearch)
            .CountAsync(cancellationToken);

        var localPage = await ProjectSalesOrderListItems(
                BuildLocalOrdersQuery(
                    unsyncedOnly: true,
                    request.Status,
                    customerSearch,
                    localFromDate,
                    localToExclusive,
                    source: null,
                    orderSearch)
                .OrderByDescending(o => o.OrderDate)
                .ThenByDescending(o => o.Id)
                .Skip(localOffset)
                .Take(pageSize),
                cancellationToken);

        var remainingSlots = Math.Max(0, pageSize - localPage.Count);
        var sapOffset = Math.Max(0, localOffset - localUnsyncedCount);

        try
        {
            var sapOrders = new List<SAPSalesOrder>();
            var sapTotalCount = 0;

            if (TryMapSapStatusFilter(request.Status, out var documentStatus, out var cancelled))
            {
                var (sapFromDate, resolvedSapToDate) = ResolveSapDateRange(customerSearch, localFromDate, sapToDate, orderSearch);
                sapTotalCount = await sapClient.GetSalesOrdersCountAsync(
                    customerSearch,
                    sapFromDate,
                    resolvedSapToDate,
                    documentStatus,
                    cancelled,
                    orderSearch,
                    cancellationToken);

                if (remainingSlots > 0)
                {
                    var fetched = 0;
                    while (fetched < remainingSlots)
                    {
                        var batch = await sapClient.GetSalesOrderHeadersAsync(
                            customerSearch,
                            sapFromDate,
                            resolvedSapToDate,
                            sapOffset + fetched,
                            Math.Min(remainingSlots - fetched, 500),
                            documentStatus,
                            cancelled,
                            orderSearch,
                            cancellationToken);

                        if (batch.Count == 0)
                            break;

                        sapOrders.AddRange(batch);
                        fetched += batch.Count;
                    }
                }
            }

            return new SalesOrderListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = sapTotalCount + localUnsyncedCount,
                TotalPages = (int)Math.Ceiling((sapTotalCount + localUnsyncedCount) / (double)pageSize),
                Orders = localPage.Concat(sapOrders.Select(MapFromSap)).ToList()
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch sales orders from SAP, falling back to local DB");
            return await GetAllFromLocalAsync(
                page,
                pageSize,
                request.Status,
                customerSearch,
                localFromDate,
                localToExclusive,
                request.Source,
                orderSearch,
                cancellationToken);
        }
    }

    private IQueryable<SalesOrderEntity> BuildLocalOrdersQuery(
        bool unsyncedOnly,
        SalesOrderStatus? status,
        string? customerSearch,
        DateTime? fromDate,
        DateTime? toExclusive,
        SalesOrderSource? source,
        string? orderSearch)
    {
        var query = context.SalesOrders.AsNoTracking().AsQueryable();

        if (unsyncedOnly)
            query = query.Where(o => !o.IsSynced);

        if (status.HasValue)
            query = query.Where(o => o.Status == status.Value);

        if (!string.IsNullOrWhiteSpace(customerSearch))
        {
            var customerPattern = $"%{customerSearch}%";
            query = query.Where(o =>
                EF.Functions.ILike(o.CardCode, customerPattern) ||
                (o.CardName != null && EF.Functions.ILike(o.CardName, customerPattern)));
        }

        if (fromDate.HasValue)
            query = query.Where(o => o.OrderDate >= fromDate.Value);

        if (toExclusive.HasValue)
            query = query.Where(o => o.OrderDate < toExclusive.Value);

        if (source.HasValue)
            query = query.Where(o => o.Source == source.Value);

        if (!string.IsNullOrWhiteSpace(orderSearch))
        {
            var searchPattern = $"%{orderSearch}%";
            if (TryParseOrderNumber(orderSearch, out var docNumber))
            {
                query = query.Where(o =>
                    EF.Functions.ILike(o.OrderNumber, searchPattern) ||
                    (o.CustomerRefNo != null && EF.Functions.ILike(o.CustomerRefNo, searchPattern)) ||
                    o.SAPDocNum == docNumber ||
                    o.SAPDocEntry == docNumber);
            }
            else
            {
                query = query.Where(o =>
                    EF.Functions.ILike(o.OrderNumber, searchPattern) ||
                    (o.CustomerRefNo != null && EF.Functions.ILike(o.CustomerRefNo, searchPattern)));
            }
        }

        return query;
    }

    private async Task<SalesOrderListResponseDto> GetAllFromLocalAsync(
        int page,
        int pageSize,
        SalesOrderStatus? status,
        string? customerSearch,
        DateTime? fromDate,
        DateTime? toExclusive,
        SalesOrderSource? source,
        string? orderSearch,
        CancellationToken cancellationToken)
    {
        var query = BuildLocalOrdersQuery(false, status, customerSearch, fromDate, toExclusive, source, orderSearch);
        var totalCount = await query.CountAsync(cancellationToken);
        var orders = await ProjectSalesOrderListItems(
            query.OrderByDescending(o => o.OrderDate)
                .ThenByDescending(o => o.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize),
            cancellationToken);

        return new SalesOrderListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Orders = orders
        };
    }

    private static async Task<List<SalesOrderDto>> ProjectSalesOrderListItems(
        IQueryable<SalesOrderEntity> query,
        CancellationToken cancellationToken)
    {
        return await query
            .Select(o => new SalesOrderDto
            {
                Id = o.Id,
                SAPDocEntry = o.SAPDocEntry,
                SAPDocNum = o.SAPDocNum,
                OrderNumber = o.OrderNumber,
                OrderDate = o.OrderDate,
                DeliveryDate = o.DeliveryDate,
                CardCode = o.CardCode,
                CardName = o.CardName,
                CustomerRefNo = o.CustomerRefNo,
                Status = o.Status,
                Comments = o.Comments,
                SalesPersonCode = o.SalesPersonCode,
                SalesPersonName = o.SalesPersonName,
                Currency = o.Currency,
                ExchangeRate = o.ExchangeRate,
                SubTotal = o.SubTotal,
                TaxAmount = o.TaxAmount,
                DiscountPercent = o.DiscountPercent,
                DiscountAmount = o.DiscountAmount,
                DocTotal = o.DocTotal,
                ShipToAddress = o.ShipToAddress,
                BillToAddress = o.BillToAddress,
                WarehouseCode = o.WarehouseCode,
                CreatedByUserId = o.CreatedByUserId,
                CreatedByUserName = o.CreatedByUser != null ? o.CreatedByUser.Username : null,
                ApprovedByUserId = o.ApprovedByUserId,
                ApprovedByUserName = o.ApprovedByUser != null ? o.ApprovedByUser.Username : null,
                ApprovedDate = o.ApprovedDate,
                CreatedAt = o.CreatedAt,
                UpdatedAt = o.UpdatedAt,
                InvoiceId = o.InvoiceId,
                IsSynced = o.IsSynced,
                SyncError = o.SyncError,
                Source = o.Source,
                ClientRequestId = o.ClientRequestId,
                MerchandiserNotes = o.MerchandiserNotes,
                DeviceInfo = o.DeviceInfo,
                Latitude = o.Latitude,
                Longitude = o.Longitude,
                RowVersion = o.RowVersion != null ? Convert.ToBase64String(o.RowVersion) : null
            })
            .ToListAsync(cancellationToken);
    }

    private static SalesOrderDto MapFromSap(SAPSalesOrder sap)
    {
        DateTime.TryParse(sap.DocDate, out var orderDate);
        DateTime.TryParse(sap.DocDueDate, out var deliveryDate);

        return new SalesOrderDto
        {
            Id = sap.DocEntry,
            SAPDocEntry = sap.DocEntry,
            SAPDocNum = sap.DocNum,
            OrderNumber = $"SAP-{sap.DocNum}",
            OrderDate = orderDate,
            DeliveryDate = deliveryDate,
            CardCode = sap.CardCode ?? string.Empty,
            CardName = sap.CardName,
            CustomerRefNo = sap.NumAtCard,
            Status = MapSapStatusToLocal(sap.DocumentStatus, sap.Cancelled),
            Comments = sap.Comments,
            SalesPersonCode = sap.SalesPersonCode,
            Currency = sap.DocCurrency,
            ExchangeRate = 1,
            SubTotal = (sap.DocTotal ?? 0) - (sap.VatSum ?? 0),
            TaxAmount = sap.VatSum ?? 0,
            DiscountPercent = sap.DiscountPercent ?? 0,
            DiscountAmount = sap.TotalDiscount ?? 0,
            DocTotal = sap.DocTotal ?? 0,
            ShipToAddress = sap.Address,
            BillToAddress = sap.Address2,
            IsSynced = true
        };
    }

    private static SalesOrderStatus MapSapStatusToLocal(string? documentStatus, string? cancelled)
    {
        if (string.Equals(cancelled, "tYES", StringComparison.OrdinalIgnoreCase))
            return SalesOrderStatus.Cancelled;

        return documentStatus switch
        {
            "bost_Open" => SalesOrderStatus.Approved,
            "bost_Close" => SalesOrderStatus.Fulfilled,
            _ => SalesOrderStatus.Approved
        };
    }

    private static (DateTime? FromDate, DateTime? ToDate) ResolveSapDateRange(
        string? customerSearch,
        DateTime? fromDate,
        DateTime? toDate,
        string? orderSearch)
    {
        if (!string.IsNullOrWhiteSpace(customerSearch) || !string.IsNullOrWhiteSpace(orderSearch) || fromDate.HasValue || toDate.HasValue)
            return (fromDate, toDate);

        var today = DateTime.UtcNow.Date;
        var startOfMonth = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return (startOfMonth, today);
    }

    private static bool TryMapSapStatusFilter(SalesOrderStatus? status, out string? documentStatus, out string? cancelled)
    {
        documentStatus = null;
        cancelled = null;

        return status switch
        {
            null => true,
            SalesOrderStatus.Approved => SetStatus("bost_Open", "tNO", out documentStatus, out cancelled),
            SalesOrderStatus.Fulfilled => SetStatus("bost_Close", "tNO", out documentStatus, out cancelled),
            SalesOrderStatus.Cancelled => SetStatus(null, "tYES", out documentStatus, out cancelled),
            _ => false
        };
    }

    private static bool SetStatus(string? documentStatusValue, string? cancelledValue, out string? documentStatus, out string? cancelled)
    {
        documentStatus = documentStatusValue;
        cancelled = cancelledValue;
        return true;
    }

    private static DateTime? NormalizeUtcDate(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);
    }

    private static string? NormalizeSearchValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TryParseOrderNumber(string search, out int docNumber)
    {
        var normalized = search.Trim();
        if (normalized.StartsWith("SAP-", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[4..];

        return int.TryParse(normalized, out docNumber);
    }
}
