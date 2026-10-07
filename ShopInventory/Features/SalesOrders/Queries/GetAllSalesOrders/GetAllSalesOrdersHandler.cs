using System.Linq.Expressions;
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

        // An open order has not been posted, so it exists only in the local tables and openOnly never
        // needs SAP either.
        if (request.Source.HasValue || request.OpenOnly)
        {
            // Deliberately no SAP work on this path. Listing source-filtered orders is answered
            // entirely from the local tables; relinking orders whose SAP metadata went missing is
            // owned by SalesOrderReconciliationJob. See the note on
            // GetSalesOrderByOrderNumberAsync for why doing it here was so expensive.
            return await GetAllFromLocalAsync(
                page,
                pageSize,
                request.Status,
                customerSearch,
                localFromDate,
                localToExclusive,
                request.Source,
                orderSearch,
                request.VanSalesUsersOnly,
                cancellationToken,
                request.OpenOnly,
                request.IncludeSummary,
                request.Columns,
                request.Sort,
                request.SortDescending,
                request.KeepOpenOrders);
        }

        var localOffset = Math.Max(0, (page - 1) * pageSize);
        var localUnsyncedCount = await BuildLocalOrdersQuery(
                unsyncedOnly: true,
                request.Status,
                customerSearch,
                localFromDate,
                localToExclusive,
                source: null,
                orderSearch,
                request.VanSalesUsersOnly)
            .CountAsync(cancellationToken);

        var localPage = await ProjectSalesOrderListItems(
                BuildLocalOrdersQuery(
                    unsyncedOnly: true,
                    request.Status,
                    customerSearch,
                    localFromDate,
                    localToExclusive,
                    source: null,
                    orderSearch,
                    request.VanSalesUsersOnly)
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
                request.VanSalesUsersOnly,
                cancellationToken,
                request.OpenOnly,
                request.IncludeSummary,
                request.Columns,
                request.Sort,
                request.SortDescending,
                request.KeepOpenOrders);
        }
    }

    private IQueryable<SalesOrderEntity> BuildLocalOrdersQuery(
        bool unsyncedOnly,
        SalesOrderStatus? status,
        string? customerSearch,
        DateTime? fromDate,
        DateTime? toExclusive,
        SalesOrderSource? source,
        string? orderSearch,
        bool? vanSalesUsersOnly,
        bool openOnly = false,
        SalesOrderColumnFilters? columns = null,
        bool keepOpenOrders = false)
    {
        var query = context.SalesOrders.AsNoTracking().AsQueryable();

        if (unsyncedOnly)
            query = query.Where(o => !o.IsSynced);

        if (openOnly)
            query = query.Where(o =>
                o.Status == SalesOrderStatus.Draft
                || o.Status == SalesOrderStatus.Pending
                || o.Status == SalesOrderStatus.OnHold
                || (o.Status == SalesOrderStatus.Approved && (o.SAPDocNum == null || o.SAPDocNum <= 0)));

        // By the status the list shows (see ProjectSalesOrderListItems), so an order only turns up under
        // the tab it reads as: an approved order that has not reached SAP reads as Pending.
        if (status.HasValue)
        {
            query = status.Value switch
            {
                SalesOrderStatus.Pending => query.Where(o => o.Status == SalesOrderStatus.Pending
                    || (o.Status == SalesOrderStatus.Approved && (o.SAPDocNum == null || o.SAPDocNum <= 0))),
                SalesOrderStatus.Approved => query.Where(o => o.Status == SalesOrderStatus.Approved && o.SAPDocNum > 0),
                _ => query.Where(o => o.Status == status.Value)
            };
        }

        if (!string.IsNullOrWhiteSpace(customerSearch))
        {
            var customerPattern = $"%{customerSearch}%";
            query = query.Where(o =>
                EF.Functions.ILike(o.CardCode, customerPattern) ||
                (o.CardName != null && EF.Functions.ILike(o.CardName, customerPattern)));
        }

        if (fromDate.HasValue)
        {
            // Mobile Orders shows a recent window, but never hides an order still waiting on someone.
            query = keepOpenOrders
                ? query.Where(o => o.OrderDate >= fromDate.Value
                    || o.Status == SalesOrderStatus.Draft
                    || o.Status == SalesOrderStatus.Pending
                    || o.Status == SalesOrderStatus.OnHold
                    || (o.Status == SalesOrderStatus.Approved && (o.SAPDocNum == null || o.SAPDocNum <= 0)))
                : query.Where(o => o.OrderDate >= fromDate.Value);
        }

        if (toExclusive.HasValue)
            query = query.Where(o => o.OrderDate < toExclusive.Value);

        if (source.HasValue)
            query = query.Where(o => o.Source == source.Value);

        if (vanSalesUsersOnly.HasValue)
        {
            const string vanSalesCommentPrefix = "Van sales sales order%";

            query = vanSalesUsersOnly.Value
                ? query.Where(o =>
                    o.Source == SalesOrderSource.Mobile
                    && ((o.Comments != null && EF.Functions.ILike(o.Comments, vanSalesCommentPrefix))
                        || (o.CustomerRefNo != null
                            && o.CustomerRefNo != string.Empty
                            && o.ClientRequestId != null
                            && o.ClientRequestId != string.Empty
                            && o.CustomerRefNo == o.ClientRequestId)))
                : query.Where(o =>
                    o.Source != SalesOrderSource.Mobile
                    || (((o.Comments == null || !EF.Functions.ILike(o.Comments, vanSalesCommentPrefix))
                            && (o.CustomerRefNo == null
                                || o.CustomerRefNo == string.Empty
                                || o.ClientRequestId == null
                                || o.ClientRequestId == string.Empty
                                || o.CustomerRefNo != o.ClientRequestId))));
        }

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

        if (columns is not null)
            query = ApplyColumnFilters(query, columns);

        return query;
    }

    /// <summary>
    /// The Mobile Orders column filters. Lower-cased <c>Contains</c> rather than ILIKE, which needs no
    /// escaping of the user's text and translates on SQLite, where the tests run, as well.
    /// </summary>
    internal static IQueryable<SalesOrderEntity> ApplyColumnFilters(IQueryable<SalesOrderEntity> query, SalesOrderColumnFilters columns)
    {
        if (!string.IsNullOrWhiteSpace(columns.OrderNumber))
        {
            var term = columns.OrderNumber.Trim().ToLower();
            query = query.Where(o => o.OrderNumber.ToLower().Contains(term));
        }

        if (columns.OrderDate is { } orderDate)
        {
            var day = DateTime.SpecifyKind(orderDate.Date, DateTimeKind.Utc);
            var nextDay = day.AddDays(1);
            query = query.Where(o => o.OrderDate >= day && o.OrderDate < nextDay);
        }

        if (columns.DeliveryDate is { } deliveryDate)
        {
            var day = DateTime.SpecifyKind(deliveryDate.Date, DateTimeKind.Utc);
            var nextDay = day.AddDays(1);
            query = query.Where(o => o.DeliveryDate >= day && o.DeliveryDate < nextDay);
        }

        if (!string.IsNullOrWhiteSpace(columns.Currency))
        {
            var currency = columns.Currency.Trim().ToLower();
            query = query.Where(o => o.Currency != null && o.Currency.ToLower() == currency);
        }

        // The page matched the total as displayed ("1,234.50"); the column's text form has no separators.
        if (!string.IsNullOrWhiteSpace(columns.Total))
        {
            var term = columns.Total.Trim().Replace(",", string.Empty);
            query = query.Where(o => o.DocTotal.ToString().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(columns.SapDocNum))
        {
            var term = columns.SapDocNum.Trim();
            query = query.Where(o => o.SAPDocNum != null && o.SAPDocNum.Value.ToString().Contains(term));
        }

        return query;
    }

    /// <summary>
    /// Sorts the local list. Id descending breaks ties so equal keys keep one order between pages.
    /// </summary>
    internal static IOrderedQueryable<SalesOrderEntity> ApplySort(
        IQueryable<SalesOrderEntity> query,
        SalesOrderListSort sort,
        bool descending)
    {
        var ordered = sort switch
        {
            SalesOrderListSort.Number => By(o => o.OrderNumber),
            SalesOrderListSort.Customer => By(o => o.CardName ?? o.CardCode),
            // An order with no delivery date sorts as the earliest, as it did in the page: last when
            // descending. PostgreSQL would put NULLs first there.
            SalesOrderListSort.Delivery => descending
                ? query.OrderBy(o => o.DeliveryDate == null).ThenByDescending(o => o.DeliveryDate)
                : query.OrderByDescending(o => o.DeliveryDate == null).ThenBy(o => o.DeliveryDate),
            SalesOrderListSort.Status => By(StatusSortOrder),
            SalesOrderListSort.Total => By(o => o.DocTotal),
            SalesOrderListSort.SapDoc => By(o => o.SAPDocNum ?? 0),
            _ => By(o => o.OrderDate)
        };

        return ordered.ThenByDescending(o => o.Id);

        IOrderedQueryable<SalesOrderEntity> By<TKey>(Expression<Func<SalesOrderEntity, TKey>> key) =>
            descending ? query.OrderByDescending(key) : query.OrderBy(key);
    }

    /// <summary>
    /// The page's status order, by the status the list shows. Delivered and Invoiced come from SAP
    /// after the page has loaded, so they sort with the status stored here.
    /// </summary>
    private static readonly Expression<Func<SalesOrderEntity, int>> StatusSortOrder = o =>
        o.Status == SalesOrderStatus.Draft ? 0
        : o.Status == SalesOrderStatus.Pending
            || (o.Status == SalesOrderStatus.Approved && (o.SAPDocNum == null || o.SAPDocNum <= 0)) ? 1
        : o.Status == SalesOrderStatus.Approved ? 2
        : o.Status == SalesOrderStatus.PartiallyFulfilled ? 3
        : o.Status == SalesOrderStatus.Fulfilled ? 4
        : o.Status == SalesOrderStatus.Cancelled ? 6
        : o.Status == SalesOrderStatus.OnHold ? 7
        : o.Status == SalesOrderStatus.Rejected ? 8
        : 9;

    private async Task<SalesOrderListResponseDto> GetAllFromLocalAsync(
        int page,
        int pageSize,
        SalesOrderStatus? status,
        string? customerSearch,
        DateTime? fromDate,
        DateTime? toExclusive,
        SalesOrderSource? source,
        string? orderSearch,
        bool? vanSalesUsersOnly,
        CancellationToken cancellationToken,
        bool openOnly = false,
        bool includeSummary = false,
        SalesOrderColumnFilters? columns = null,
        SalesOrderListSort sort = SalesOrderListSort.Ordered,
        bool sortDescending = true,
        bool keepOpenOrders = false)
    {
        var query = BuildLocalOrdersQuery(false, status, customerSearch, fromDate, toExclusive, source, orderSearch, vanSalesUsersOnly, openOnly, columns, keepOpenOrders);
        var totalCount = await query.CountAsync(cancellationToken);
        var orders = await ProjectSalesOrderListItems(
            ApplySort(query, sort, sortDescending)
                .Skip((page - 1) * pageSize)
                .Take(pageSize),
            cancellationToken);

        return new SalesOrderListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Orders = orders,
            Summary = includeSummary
                ? await BuildSummaryAsync(source, vanSalesUsersOnly, cancellationToken)
                : null
        };
    }

    /// <summary>
    /// All-time counts for the orders a source and view cover, whatever the page's other filters.
    /// </summary>
    /// <remarks>
    /// The Mobile Orders page used to load every order ever made so that its tiles could count them.
    /// It now loads the recent and the open ones, and reads the counts from here. Pending and Approved
    /// are the statuses the list shows (see <see cref="ProjectSalesOrderListItems"/>): an approved order
    /// that has not reached SAP reads as Pending.
    /// </remarks>
    private async Task<SalesOrderListSummaryDto> BuildSummaryAsync(
        SalesOrderSource? source,
        bool? vanSalesUsersOnly,
        CancellationToken cancellationToken)
    {
        var allOrders = BuildLocalOrdersQuery(false, null, null, null, null, source, null, vanSalesUsersOnly);
        var summary = await SummaryQuery(allOrders)
            // Single, not First: one group is one row or none.
            .SingleOrDefaultAsync(cancellationToken)
            ?? new SalesOrderListSummaryDto();

        // The page's currency filter and status tabs offer only what exists. It used to read them off
        // every order it held; it now holds one page.
        var currencies = await allOrders
            .Where(o => o.Currency != null && o.Currency != string.Empty)
            .Select(o => o.Currency!)
            .Distinct()
            .ToListAsync(cancellationToken);
        summary.Currencies = currencies
            .Select(currency => currency.Trim())
            .Where(currency => currency.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        summary.Statuses = await allOrders
            .Select(o => o.Status == SalesOrderStatus.Approved && (o.SAPDocNum == null || o.SAPDocNum <= 0)
                ? SalesOrderStatus.Pending
                : o.Status)
            .Distinct()
            .OrderBy(status => status)
            .ToListAsync(cancellationToken);

        return summary;
    }

    /// <summary>The counts <see cref="BuildSummaryAsync"/> reads, as one grouped query.</summary>
    internal static IQueryable<SalesOrderListSummaryDto> SummaryQuery(IQueryable<SalesOrderEntity> source) =>
        source
            .GroupBy(_ => 1)
            .Select(orders => new SalesOrderListSummaryDto
            {
                Total = orders.Count(),
                Draft = orders.Count(o => o.Status == SalesOrderStatus.Draft),
                Pending = orders.Count(o => o.Status == SalesOrderStatus.Pending
                    || (o.Status == SalesOrderStatus.Approved && (o.SAPDocNum == null || o.SAPDocNum <= 0))),
                Approved = orders.Count(o => o.Status == SalesOrderStatus.Approved && o.SAPDocNum > 0),
                OldestPendingCreatedAt = orders.Min(o =>
                    o.Status == SalesOrderStatus.Pending
                    || (o.Status == SalesOrderStatus.Approved && (o.SAPDocNum == null || o.SAPDocNum <= 0))
                        ? (DateTime?)o.CreatedAt
                        : null)
            });

    private static async Task<List<SalesOrderDto>> ProjectSalesOrderListItems(
        IQueryable<SalesOrderEntity> query,
        CancellationToken cancellationToken)
    {
        return await query
            .Select(o => new SalesOrderDto
            {
                Status = o.Status == SalesOrderStatus.Approved && (!o.SAPDocNum.HasValue || o.SAPDocNum <= 0)
                    ? SalesOrderStatus.Pending
                    : o.Status,
                Id = o.Id,
                SAPDocEntry = o.SAPDocEntry,
                SAPDocNum = o.SAPDocNum,
                OrderNumber = o.OrderNumber,
                OrderDate = o.OrderDate,
                DeliveryDate = o.DeliveryDate,
                CardCode = o.CardCode,
                CardName = o.CardName,
                CustomerRefNo = o.CustomerRefNo,
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
                InvoiceSapDocNum = o.Invoice != null ? o.Invoice.SAPDocNum : null,
                IsSynced = o.IsSynced && o.SAPDocNum.HasValue && o.SAPDocNum > 0,
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
