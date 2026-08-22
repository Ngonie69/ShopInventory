using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Validation;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Middleware;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Service implementation for Sales Order operations - Fetches from SAP Business One
/// </summary>
public class SalesOrderService : ISalesOrderService
{
    private const int MaxCommentsLength = 1000;
    private const int MaxCreateOrderAttempts = 5;

    // How many order numbers a reconciliation sweep names before summarising the rest. Enough to
    // act on without turning a two-minute job into a wall of text.
    private const int MaxReportedStuckOrderNumbers = 10;

    /// <summary>
    /// How old an unlinked order has to be before a sweep reports it as needing a person rather than
    /// as something still settling.
    /// </summary>
    /// <remarks>
    /// A SAP create that committed is visible within moments, so an order still unlinked an hour
    /// later was never accepted and no number of further probes will change that. SO-20260817-0008
    /// was probed every two minutes for three days without one line saying so above Information.
    /// </remarks>
    private static readonly TimeSpan StuckCandidateAge = TimeSpan.FromHours(1);
    private const string SapPostingIdempotencyScope = "salesorders.sap-post";
    private const string SapPostingUncertainSyncErrorPrefix = "The SAP posting result is uncertain";
    private static readonly TimeSpan ApprovalPricingTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SapPostingUncertainRetryHold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PostingLockAcquireWait = TimeSpan.FromSeconds(5);

    private readonly ApplicationDbContext _context;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<SalesOrderService> _logger;
    private readonly INotificationService _notificationService;
    private readonly IBusinessPartnerService _businessPartnerService;
    private readonly ILocalPriceCatalogService _localPriceCatalogService;
    private readonly IIdempotencyRequestStore _idempotencyRequestStore;
    private readonly ICreditLimitService _creditLimitService;
    private readonly decimal _defaultTaxPercent;
    private readonly string _connectionString;

    public SalesOrderService(
        ApplicationDbContext context,
        ISAPServiceLayerClient sapClient,
        ILogger<SalesOrderService> logger,
        INotificationService notificationService,
        IBusinessPartnerService businessPartnerService,
        ILocalPriceCatalogService localPriceCatalogService,
        IIdempotencyRequestStore idempotencyRequestStore,
        ICreditLimitService creditLimitService,
        IOptions<TaxSettings> taxSettings)
    {
        _context = context;
        _sapClient = sapClient;
        _logger = logger;
        _notificationService = notificationService;
        _businessPartnerService = businessPartnerService;
        _localPriceCatalogService = localPriceCatalogService;
        _idempotencyRequestStore = idempotencyRequestStore;
        _creditLimitService = creditLimitService;
        _defaultTaxPercent = NormalizeTaxPercent(taxSettings.Value.VatRate);
        _connectionString = context.Database.GetConnectionString()
            ?? throw new InvalidOperationException("DefaultConnection is not configured.");
    }

    public async Task<SalesOrderDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        // Try to get from SAP first (by DocEntry)
        try
        {
            var sapOrder = await _sapClient.GetSalesOrderByDocEntryAsync(id, cancellationToken);
            if (sapOrder != null)
            {
                var sapDto = MapFromSAP(sapOrder);
                await ApplyLocalAuditTrailAsync(sapDto, cancellationToken);
                return sapDto;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch sales order {Id} from SAP, falling back to local DB", id);
        }

        // Fallback to local database
        var order = await _context.SalesOrders
            .Include(o => o.Lines)
            .Include(o => o.CreatedByUser)
            .Include(o => o.ApprovedByUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id || o.SAPDocEntry == id, cancellationToken);

        return order == null ? null : MapToDto(order);
    }

    public async Task<SalesOrderDto?> GetByOrderNumberAsync(string orderNumber, CancellationToken cancellationToken = default)
    {
        // SAP doesn't have a direct OrderNumber query, so use local DB
        var order = await _context.SalesOrders
            .Include(o => o.Lines)
            .Include(o => o.CreatedByUser)
            .Include(o => o.ApprovedByUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber, cancellationToken);

        return order == null ? null : MapToDto(order);
    }

    public async Task<SalesOrderListResponseDto> GetAllAsync(int page, int pageSize, SalesOrderStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null,
        SalesOrderSource? source = null, string? search = null, CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 10000);

        // Normalize dates to UTC for Npgsql timestamptz compatibility
        if (fromDate.HasValue && fromDate.Value.Kind == DateTimeKind.Unspecified)
            fromDate = DateTime.SpecifyKind(fromDate.Value, DateTimeKind.Utc);
        if (toDate.HasValue && toDate.Value.Kind == DateTimeKind.Unspecified)
            toDate = DateTime.SpecifyKind(toDate.Value, DateTimeKind.Utc);

        // Mobile/source-filtered orders are local-only — SAP has no source concept
        if (source.HasValue)
        {
            _logger.LogInformation("Fetching {Source} orders from local DB", source.Value);
            return await GetAllFromLocalAsync(page, pageSize, status, cardCode, fromDate, toDate, source, search, cancellationToken);
        }

        var localOffset = Math.Max(0, (page - 1) * pageSize);
        var localUnsyncedCount = await GetLocalUnsyncedOrdersCountAsync(status, cardCode, fromDate, toDate, search, cancellationToken);
        var localPage = await GetLocalUnsyncedOrdersPageAsync(localOffset, pageSize, status, cardCode, fromDate, toDate, search, cancellationToken);
        var remainingSlots = Math.Max(0, pageSize - localPage.Count);
        var sapOffset = Math.Max(0, localOffset - localUnsyncedCount);

        try
        {
            _logger.LogInformation("Fetching sales orders from SAP - Page: {Page}, PageSize: {PageSize}", page, pageSize);

            var sapOrders = new List<SAPSalesOrder>();
            var sapTotalCount = 0;

            if (TryMapSapStatusFilter(status, out var documentStatus, out var cancelled))
            {
                var (sapFromDate, sapToDate) = ResolveSapDateRange(cardCode, fromDate, toDate);
                sapTotalCount = await _sapClient.GetSalesOrdersCountAsync(
                    cardCode,
                    sapFromDate,
                    sapToDate,
                    documentStatus,
                    cancelled,
                    search: null,
                    cancellationToken);

                if (remainingSlots > 0)
                {
                    int fetched = 0;
                    while (fetched < remainingSlots)
                    {
                        var batch = await _sapClient.GetSalesOrderHeadersAsync(
                            cardCode,
                            sapFromDate,
                            sapToDate,
                            sapOffset + fetched,
                            Math.Min(remainingSlots - fetched, 500),
                            documentStatus,
                            cancelled,
                            search: null,
                            cancellationToken);

                        if (batch.Count == 0) break;
                        sapOrders.AddRange(batch);
                        fetched += batch.Count;
                    }
                }
            }

            var result = new SalesOrderListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = sapTotalCount + localUnsyncedCount,
                TotalPages = (int)Math.Ceiling((sapTotalCount + localUnsyncedCount) / (double)pageSize),
                Orders = localPage.Concat(sapOrders.Select(MapFromSAP)).ToList()
            };

            _logger.LogInformation("Returning {OrderCount} sales orders, TotalCount: {TotalCount}, TotalPages: {TotalPages}",
                result.Orders.Count, result.TotalCount, result.TotalPages);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch sales orders from SAP, falling back to local DB");
            return await GetAllFromLocalAsync(page, pageSize, status, cardCode, fromDate, toDate, source, search, cancellationToken);
        }
    }

    private IQueryable<SalesOrderEntity> BuildLocalUnsyncedOrdersQuery(SalesOrderStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null,
        string? search = null, CancellationToken cancellationToken = default)
    {
        var query = _context.SalesOrders
            .AsNoTracking()
            .Where(o => !o.IsSynced)
            .AsQueryable();

        if (status.HasValue)
        {
            query = status.Value switch
            {
                SalesOrderStatus.Approved => query.Where(o => o.Status == SalesOrderStatus.Approved && o.SAPDocNum.HasValue),
                SalesOrderStatus.Pending => query.Where(o => o.Status == SalesOrderStatus.Pending || (o.Status == SalesOrderStatus.Approved && !o.SAPDocNum.HasValue)),
                _ => query.Where(o => o.Status == status.Value)
            };
        }

        if (!string.IsNullOrEmpty(cardCode))
            query = query.Where(o => o.CardCode == cardCode);

        if (fromDate.HasValue)
            query = query.Where(o => o.OrderDate >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(o => o.OrderDate <= toDate.Value);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(o =>
                (o.OrderNumber != null && o.OrderNumber.Contains(search)) ||
                (o.CardCode != null && o.CardCode.Contains(search)) ||
                (o.CardName != null && o.CardName.Contains(search)) ||
                (o.CustomerRefNo != null && o.CustomerRefNo.Contains(search)));

        return query;
    }

    private async Task<int> GetLocalUnsyncedOrdersCountAsync(SalesOrderStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null,
        string? search = null, CancellationToken cancellationToken = default)
    {
        return await BuildLocalUnsyncedOrdersQuery(status, cardCode, fromDate, toDate, search, cancellationToken)
            .CountAsync(cancellationToken);
    }

    private async Task<List<SalesOrderDto>> GetLocalUnsyncedOrdersPageAsync(int skip, int take,
        SalesOrderStatus? status = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null,
        string? search = null, CancellationToken cancellationToken = default)
    {
        if (take <= 0)
            return new List<SalesOrderDto>();

        var orders = await BuildLocalUnsyncedOrdersQuery(status, cardCode, fromDate, toDate, search, cancellationToken)
            .OrderByDescending(o => o.OrderDate)
            .ThenByDescending(o => o.Id)
            .Skip(skip)
            .Take(take)
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
                Status = o.Status == SalesOrderStatus.Approved && !o.SAPDocNum.HasValue ? SalesOrderStatus.Pending : o.Status,
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
                IsSynced = o.IsSynced,
                SyncError = o.SyncError,
                Source = o.Source,
                MerchandiserNotes = o.MerchandiserNotes,
                DeviceInfo = o.DeviceInfo,
                Latitude = o.Latitude,
                Longitude = o.Longitude,
                RowVersion = o.RowVersion != null ? Convert.ToBase64String(o.RowVersion) : null,
                Lines = o.Lines.Select(l => new SalesOrderLineDto
                {
                    Id = l.Id,
                    LineNum = l.LineNum,
                    ItemCode = l.ItemCode,
                    ItemDescription = l.ItemDescription,
                    Quantity = l.Quantity,
                    QuantityFulfilled = l.QuantityFulfilled,
                    UnitPrice = l.UnitPrice,
                    DiscountPercent = l.DiscountPercent,
                    TaxPercent = l.TaxPercent,
                    LineTotal = l.LineTotal,
                    WarehouseCode = l.WarehouseCode,
                    UoMCode = l.UoMCode,
                    BatchNumber = l.BatchNumber,
                    CostCentreCode = l.CostCentreCode
                }).ToList()
            })
            .ToListAsync(cancellationToken);

        return orders;
    }

    private async Task<SalesOrderListResponseDto> GetAllFromLocalAsync(int page, int pageSize, SalesOrderStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null,
        SalesOrderSource? source = null, string? search = null, CancellationToken cancellationToken = default)
    {
        var query = _context.SalesOrders
            .AsNoTracking()
            .AsQueryable();
        if (status.HasValue)
        {
            query = status.Value switch
            {
                SalesOrderStatus.Approved => query.Where(o => o.Status == SalesOrderStatus.Approved && o.SAPDocNum.HasValue),
                SalesOrderStatus.Pending => query.Where(o => o.Status == SalesOrderStatus.Pending || (o.Status == SalesOrderStatus.Approved && !o.SAPDocNum.HasValue)),
                _ => query.Where(o => o.Status == status.Value)
            };
        }

        if (!string.IsNullOrEmpty(cardCode))
            query = query.Where(o => o.CardCode == cardCode);

        if (fromDate.HasValue)
            query = query.Where(o => o.OrderDate >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(o => o.OrderDate <= toDate.Value);

        if (source.HasValue)
            query = query.Where(o => o.Source == source.Value);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(o =>
                (o.OrderNumber != null && o.OrderNumber.Contains(search)) ||
                (o.CardCode != null && o.CardCode.Contains(search)) ||
                (o.CardName != null && o.CardName.Contains(search)) ||
                (o.CustomerRefNo != null && o.CustomerRefNo.Contains(search)));

        var totalCount = await query.CountAsync(cancellationToken);
        var pageOrderIds = await query
            .OrderByDescending(o => o.OrderDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        await RefreshLocalSalesOrderSnapshotsFromSapAsync(pageOrderIds, cancellationToken);

        var orders = await _context.SalesOrders
            .AsNoTracking()
            .Where(o => pageOrderIds.Contains(o.Id))
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
                Status = o.Status == SalesOrderStatus.Approved && !o.SAPDocNum.HasValue ? SalesOrderStatus.Pending : o.Status,
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
                IsSynced = o.IsSynced,
                SyncError = o.SyncError,
                Source = o.Source,
                MerchandiserNotes = o.MerchandiserNotes,
                DeviceInfo = o.DeviceInfo,
                Latitude = o.Latitude,
                Longitude = o.Longitude,
                RowVersion = o.RowVersion != null ? Convert.ToBase64String(o.RowVersion) : null,
                Lines = o.Lines.Select(l => new SalesOrderLineDto
                {
                    Id = l.Id,
                    LineNum = l.LineNum,
                    ItemCode = l.ItemCode,
                    ItemDescription = l.ItemDescription,
                    Quantity = l.Quantity,
                    QuantityFulfilled = l.QuantityFulfilled,
                    UnitPrice = l.UnitPrice,
                    DiscountPercent = l.DiscountPercent,
                    TaxPercent = l.TaxPercent,
                    LineTotal = l.LineTotal,
                    WarehouseCode = l.WarehouseCode,
                    UoMCode = l.UoMCode,
                    BatchNumber = l.BatchNumber,
                    CostCentreCode = l.CostCentreCode
                }).ToList()
            })
            .ToListAsync(cancellationToken);

        var orderLookup = orders.ToDictionary(order => order.Id);
        var orderedPage = pageOrderIds
            .Where(orderLookup.ContainsKey)
            .Select(id => orderLookup[id])
            .ToList();

        return new SalesOrderListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Orders = orderedPage
        };
    }

    public async Task<SalesOrderDto> CreateAsync(CreateSalesOrderRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var clientRequestId = string.IsNullOrWhiteSpace(request.ClientRequestId) ? null : request.ClientRequestId.Trim();
        if (!string.IsNullOrWhiteSpace(clientRequestId))
        {
            var existingOrder = await _context.SalesOrders
                .AsNoTracking()
                .Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.ClientRequestId == clientRequestId, cancellationToken);

            if (existingOrder != null)
            {
                return MapToDto(existingOrder);
            }
        }

        await ValidateAndNormalizeSalesOrderRequestAsync(request, cancellationToken);

        // Refuse an over-limit order before anything is written. Posting is also gated, but a web
        // order that fails there is only left as a Draft with a sync error, which the rep never
        // sees — the refusal has to reach them here, while they are still holding the order.
        //
        // A mobile order is exempt. Its lines arrive unpriced, so this check can only ever measure
        // the customer's existing balance, and refusing on that destroys the capture: the order
        // never reaches the web platform, and the rep in the field is left with a failed draft and
        // advice ("reduce the order") that cannot change the number it was refused on. Mobile
        // orders are captured instead and held on the web, where the gate in
        // PostApprovedOrderToSapCoreAsync — the one that runs against the real DocTotal — stops
        // them reaching SAP while the account is over.
        if (request.Source != SalesOrderSource.Mobile)
        {
            await EnsureWithinCreditLimitAsync(
                request.CardCode,
                CalculateSalesOrderTotals(request.Lines, request.DiscountPercent).DocTotal,
                cancellationToken);
        }

        // If no warehouse specified, fall back to the user's assigned warehouse
        if (string.IsNullOrEmpty(request.WarehouseCode))
        {
            var user = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .FirstOrDefaultAsync(cancellationToken);

            if (user != null)
            {
                request.WarehouseCode = user.GetWarehouseCodes().FirstOrDefault();
            }
        }

        for (var attempt = 1; attempt <= MaxCreateOrderAttempts; attempt++)
        {
            var orderNumber = await GenerateOrderNumberAsync(cancellationToken);
            SalesOrderEntity order;
            int persistedLineCount;

            await using (var transaction = await _context.Database.BeginTransactionAsync(cancellationToken))
            {
                try
                {
                    var initialStatus = request.Source == SalesOrderSource.Mobile
                        ? SalesOrderStatus.Pending
                        : SalesOrderStatus.Draft;

                    order = new SalesOrderEntity
                    {
                        OrderNumber = orderNumber,
                        OrderDate = DateTime.UtcNow,
                        DeliveryDate = request.DeliveryDate.HasValue
                            ? DateTime.SpecifyKind(request.DeliveryDate.Value, DateTimeKind.Utc)
                            : null,
                        CardCode = request.CardCode,
                        CardName = request.CardName,
                        // Van sales only, already validated by the handler that set them: CardCode above
                        // is the van's own business partner on those orders, not the shop.
                        RouteCustomerId = request.RouteCustomerId,
                        RouteCustomerCode = request.RouteCustomerCode,
                        RouteCustomerName = request.RouteCustomerName,
                        CustomerRefNo = request.CustomerRefNo,
                        Comments = request.Comments,
                        SalesPersonCode = request.SalesPersonCode,
                        SalesPersonName = request.SalesPersonName,
                        Currency = request.Currency!,
                        DiscountPercent = request.DiscountPercent,
                        ShipToAddress = request.ShipToAddress,
                        BillToAddress = request.BillToAddress,
                        WarehouseCode = request.WarehouseCode,
                        CreatedByUserId = userId,
                        Status = initialStatus,
                        Source = request.Source,
                        ClientRequestId = string.IsNullOrWhiteSpace(request.ClientRequestId) ? null : request.ClientRequestId.Trim(),
                        MerchandiserNotes = request.MerchandiserNotes,
                        DeviceInfo = request.DeviceInfo,
                        Latitude = request.Latitude,
                        Longitude = request.Longitude
                    };

                    int lineNum = 0;

                    foreach (var lineRequest in request.Lines)
                    {
                        var line = new SalesOrderLineEntity
                        {
                            LineNum = lineNum++,
                            ItemCode = lineRequest.ItemCode,
                            ItemDescription = lineRequest.ItemDescription,
                            Quantity = lineRequest.Quantity,
                            UnitPrice = lineRequest.UnitPrice,
                            DiscountPercent = lineRequest.DiscountPercent,
                            TaxPercent = ResolveLineTaxPercent(lineRequest.TaxPercent),
                            LineTotal = CalculateLineTotal(lineRequest.Quantity, lineRequest.UnitPrice, lineRequest.DiscountPercent),
                            WarehouseCode = !string.IsNullOrEmpty(lineRequest.WarehouseCode) ? lineRequest.WarehouseCode : request.WarehouseCode,
                            UoMCode = lineRequest.UoMCode,
                            BatchNumber = lineRequest.BatchNumber,
                            CostCentreCode = lineRequest.CostCentreCode
                        };

                        order.Lines.Add(line);
                    }

                    // Same helper the credit check above ran on, so the total this order is refused
                    // or accepted on is the total it is stored with.
                    var totals = CalculateSalesOrderTotals(request.Lines, request.DiscountPercent);
                    order.SubTotal = totals.SubTotal;
                    order.TaxAmount = totals.TaxAmount;
                    order.DiscountAmount = totals.DiscountAmount;
                    order.DocTotal = totals.DocTotal;

                    _context.SalesOrders.Add(order);
                    await _context.SaveChangesAsync(cancellationToken);

                    persistedLineCount = await _context.SalesOrderLines
                        .AsNoTracking()
                        .CountAsync(l => l.SalesOrderId == order.Id, cancellationToken);

                    if (persistedLineCount != request.Lines.Count)
                    {
                        throw new InvalidOperationException(
                            $"Sales order line persistence mismatch for {orderNumber}. Expected {request.Lines.Count} lines but saved {persistedLineCount}.");
                    }

                    if (request.Source == SalesOrderSource.Mobile)
                    {
                        _context.MobileOrderPostProcessingQueue.Add(new MobileOrderPostProcessingQueueEntity
                        {
                            SalesOrderId = order.Id,
                            OrderNumber = order.OrderNumber,
                            LineCount = persistedLineCount,
                            MaxRetries = 5,
                            CreatedAt = DateTime.UtcNow
                        });

                        await _context.SaveChangesAsync(cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (IsDuplicateClientRequestId(ex) && !string.IsNullOrWhiteSpace(clientRequestId))
                {
                    await TryRollbackCreateTransactionAsync(transaction);
                    _context.ChangeTracker.Clear();

                    var existingOrder = await _context.SalesOrders
                        .AsNoTracking()
                        .Include(o => o.Lines)
                        .FirstOrDefaultAsync(o => o.ClientRequestId == clientRequestId, CancellationToken.None);

                    if (existingOrder != null)
                        return MapToDto(existingOrder);

                    throw;
                }
                catch (DbUpdateException ex) when (IsDuplicateOrderNumber(ex) && attempt < MaxCreateOrderAttempts)
                {
                    await TryRollbackCreateTransactionAsync(transaction);
                    _context.ChangeTracker.Clear();

                    _logger.LogWarning(ex,
                        "Order number collision while creating sales order for customer {CardCode}. Retrying attempt {Attempt} of {MaxAttempts}.",
                        request.CardCode,
                        attempt,
                        MaxCreateOrderAttempts);
                    continue;
                }
                catch (Exception ex)
                {
                    await TryRollbackCreateTransactionAsync(transaction, ex);
                    throw;
                }
            }

            _logger.LogInformation(
                "Created sales order {OrderNumber} for customer {CardCode} with {LineCount} lines",
                orderNumber,
                request.CardCode,
                persistedLineCount);

            if (request.Source == SalesOrderSource.Mobile)
            {
                return MapToDto(order);
            }

            await SendSalesOrderNotificationAsync(order, cancellationToken);

            if (request.Source == SalesOrderSource.Web)
            {
                try
                {
                    await using var postingLock = await AcquireSalesOrderPostingLockAsync(order.OrderNumber, cancellationToken);
                    order = await LoadSalesOrderForPostingAsync(order.Id, cancellationToken);

                    if (order.Status != SalesOrderStatus.Draft)
                    {
                        throw new InvalidOperationException(
                            $"Sales order {order.OrderNumber} can no longer be auto-posted because its current status is {order.Status}.");
                    }

                    order.ApprovedByUserId = userId;
                    order.ApprovedDate = DateTime.UtcNow;
                    await PostApprovedOrderToSapAsync(order, cancellationToken, postingLockHeld: true);

                    _logger.LogInformation("Auto-posted sales order {OrderNumber} to SAP as DocEntry={DocEntry}, DocNum={DocNum}",
                        order.OrderNumber, order.SAPDocEntry, order.SAPDocNum);
                }
                catch (Exception ex)
                {
                    // The post may have linked a real SAP document before failing at a later
                    // step. Discarding that link would strand the order as an unposted Draft
                    // while SAP holds the document, so keep the linkage and report success.
                    if (HasSapDocNum(order))
                    {
                        order.IsSynced = true;
                        order.SyncError = null;
                        order.UpdatedAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync(CancellationToken.None);

                        _logger.LogWarning(
                            ex,
                            "Auto-post to SAP for order {OrderNumber} reported a failure but the order is linked to DocEntry={DocEntry}, DocNum={DocNum}. Keeping the SAP link.",
                            order.OrderNumber,
                            order.SAPDocEntry,
                            order.SAPDocNum);
                    }
                    else if (order.Status != SalesOrderStatus.Cancelled)
                    {
                        order.Status = SalesOrderStatus.Draft;
                        order.ApprovedByUserId = null;
                        order.ApprovedDate = null;
                        order.SAPDocEntry = null;
                        order.SAPDocNum = null;
                        order.IsSynced = false;
                        order.SyncError = TruncateSyncError(ex.Message);
                        order.UpdatedAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync(CancellationToken.None);

                        _logger.LogWarning(ex, "Auto-post to SAP failed for order {OrderNumber}. Order remains Draft.", order.OrderNumber);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Skipped auto-post recovery update for cancelled sales order {OrderNumber}",
                            order.OrderNumber);
                    }
                }
            }

            return MapToDto(order);
        }

        throw new InvalidOperationException("Failed to create sales order after retrying order number generation.");
    }

    public async Task ProcessMobileOrderPostSaveAsync(int id, CancellationToken cancellationToken = default)
    {
        var queueEntry = await _context.MobileOrderPostProcessingQueue
            .AsTracking()
            .FirstOrDefaultAsync(q => q.SalesOrderId == id, cancellationToken);

        if (queueEntry == null)
        {
            _logger.LogWarning("Skipped post-processing for mobile sales order {OrderId} because no durable queue entry exists", id);
            return;
        }

        if (queueEntry.Status is MobileOrderPostProcessingQueueStatus.Completed or MobileOrderPostProcessingQueueStatus.Cancelled)
        {
            return;
        }

        var order = await _context.SalesOrders
            .AsTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
        {
            await MarkMobileOrderPostProcessingFailureAsync(
                queueEntry,
                new InvalidOperationException($"Sales order {id} was not found for mobile post-save processing."),
                cancellationToken);
            return;
        }

        if (order.Source != SalesOrderSource.Mobile)
        {
            queueEntry.Status = MobileOrderPostProcessingQueueStatus.Completed;
            queueEntry.ProcessedAt = DateTime.UtcNow;
            queueEntry.LastError = null;
            queueEntry.NextRetryAt = null;
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        try
        {
            if (queueEntry.PricesResolvedAt == null)
            {
                await ResolveMobileOrderPricesAsync(order, cancellationToken);
                await RecordMobileOrderCreditPositionAsync(order, cancellationToken);

                queueEntry.PricesResolvedAt = DateTime.UtcNow;
                queueEntry.LastError = null;
                queueEntry.NextRetryAt = null;
                await _context.SaveChangesAsync(cancellationToken);
            }

            if (queueEntry.NotificationSentAt == null)
            {
                await SendSalesOrderNotificationAsync(order, cancellationToken, suppressErrors: false);
                queueEntry.NotificationSentAt = DateTime.UtcNow;
                queueEntry.LastError = null;
                await _context.SaveChangesAsync(cancellationToken);
            }

            queueEntry.Status = MobileOrderPostProcessingQueueStatus.Completed;
            queueEntry.ProcessedAt = DateTime.UtcNow;
            queueEntry.NextRetryAt = null;
            queueEntry.LastError = null;
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Completed durable post-save processing for mobile sales order {OrderNumber} ({OrderId})",
                order.OrderNumber,
                order.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await MarkMobileOrderPostProcessingFailureAsync(queueEntry, ex, cancellationToken);
        }
    }

    private async Task ResolveMobileOrderPricesAsync(SalesOrderEntity order, CancellationToken cancellationToken)
    {
        var itemCodes = order.Lines
            .Select(l => l.ItemCode)
            .Where(itemCode => !string.IsNullOrWhiteSpace(itemCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (itemCodes.Count == 0)
        {
            _logger.LogWarning("Mobile order {OrderNumber} has no item codes to resolve prices for", order.OrderNumber);
            return;
        }

        var pricing = await _localPriceCatalogService.GetBusinessPartnerPricingAsync(order.CardCode, itemCodes, cancellationToken);
        var prices = pricing?.Prices.Prices;
        if (prices == null || prices.Count == 0)
        {
            _logger.LogWarning("No locally stored prices returned for customer {CardCode} items on mobile order {OrderNumber}", order.CardCode, order.OrderNumber);
            return;
        }

        var priceLookup = prices
            .Where(price => !string.IsNullOrWhiteSpace(price.ItemCode) && price.Price > 0)
            .ToDictionary(price => price.ItemCode ?? string.Empty, price => price.Price, StringComparer.OrdinalIgnoreCase);

        decimal subTotal = 0;
        decimal taxAmount = 0;
        int updatedLines = 0;

        foreach (var line in order.Lines)
        {
            line.TaxPercent = ResolveLineTaxPercent(line.TaxPercent);

            if (priceLookup.TryGetValue(line.ItemCode, out var price) && price > 0)
            {
                line.UnitPrice = price;
                line.LineTotal = line.Quantity * price * (1 - line.DiscountPercent / 100);
                updatedLines++;
            }

            var lineTax = line.LineTotal * line.TaxPercent / 100;
            subTotal += line.LineTotal;
            taxAmount += lineTax;
        }

        order.SubTotal = subTotal;
        order.TaxAmount = taxAmount;
        order.DiscountAmount = subTotal * order.DiscountPercent / 100;
        order.DocTotal = subTotal - order.DiscountAmount + taxAmount;
        order.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Resolved prices for mobile order {OrderNumber}: {UpdatedLines}/{TotalLines} lines updated for customer {CardCode}",
            order.OrderNumber, updatedLines, order.Lines.Count, order.CardCode);
    }

    /// <summary>
    /// Notes on a freshly priced mobile order whether the customer is over its credit limit, so the
    /// web platform can say why the order will not post before anyone tries to approve it.
    /// </summary>
    /// <remarks>
    /// Advisory only — the refusal itself belongs to the gate in
    /// <c>PostApprovedOrderToSapCoreAsync</c>, which re-reads SAP at the moment of posting. This
    /// note is a snapshot taken at pricing time and goes stale the moment a payment lands, which is
    /// why it is worded as one and why it never blocks anything on its own. Never throws: a mobile
    /// order that could not be measured is still a captured order.
    /// </remarks>
    private async Task RecordMobileOrderCreditPositionAsync(SalesOrderEntity order, CancellationToken cancellationToken)
    {
        if (order.IsSynced || HasSapDocNum(order))
        {
            return;
        }

        CreditLimitCheckResult result;
        try
        {
            result = await _creditLimitService.CheckSalesOrderAsync(order.CardCode, order.DocTotal, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not measure the credit position for mobile sales order {OrderNumber}; it was captured without a credit note",
                order.OrderNumber);
            return;
        }

        if (result.IsWithinLimit)
        {
            return;
        }

        // The hold leads, because a long group message plus a long account name can run past the
        // 500-character column: what gets cut then is the tail of the credit detail (the advice to
        // take a payment), not the statement of why the order is sitting there.
        order.SyncError = TruncateSyncError(
            "Held on credit — this order cannot be posted to SAP until the account is inside its limit. " +
            (result.Message ?? "It would take the customer over its credit limit."));
        order.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Mobile sales order {OrderNumber} for {CardCode} was captured over the credit limit: exposure {Exposure} against a {CreditLimit} limit on {CreditAccount}. It will not post until the account is inside its limit.",
            order.OrderNumber,
            order.CardCode,
            result.Exposure,
            result.CreditLimit,
            result.CreditAccountCardCode);
    }

    private async Task SendSalesOrderNotificationAsync(SalesOrderEntity order, CancellationToken cancellationToken, bool suppressErrors = true)
    {
        try
        {
            var username = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == order.CreatedByUserId)
                .Select(u => u.Username)
                .FirstOrDefaultAsync(cancellationToken);

            var source = order.Source == SalesOrderSource.Mobile ? "Mobile" : "Web";
            await _notificationService.CreateSalesOrderNotificationAsync(
                order.Id,
                order.OrderNumber,
                order.CardCode,
                order.CardName ?? order.CardCode,
                order.DocTotal,
                order.Status.ToString(),
                source,
                username,
                cancellationToken);
        }
        catch (Exception ex)
        {
            if (suppressErrors)
            {
                _logger.LogWarning(ex, "Failed to send notification for sales order {OrderNumber}", order.OrderNumber);
                return;
            }

            throw;
        }
    }

    private async Task MarkMobileOrderPostProcessingFailureAsync(
        MobileOrderPostProcessingQueueEntity queueEntry,
        Exception ex,
        CancellationToken cancellationToken)
    {
        queueEntry.RetryCount++;
        queueEntry.LastError = TruncateMobileQueueError(ex.Message);
        queueEntry.ProcessingStartedAt = null;

        if (queueEntry.RetryCount >= queueEntry.MaxRetries)
        {
            queueEntry.Status = MobileOrderPostProcessingQueueStatus.RequiresReview;
            queueEntry.ProcessedAt = DateTime.UtcNow;
            queueEntry.NextRetryAt = null;
        }
        else
        {
            queueEntry.Status = MobileOrderPostProcessingQueueStatus.Failed;
            queueEntry.ProcessedAt = null;
            var delaySeconds = 30 * Math.Pow(2, queueEntry.RetryCount - 1);
            queueEntry.NextRetryAt = DateTime.UtcNow.AddSeconds(delaySeconds);
        }

        await _context.SaveChangesAsync(cancellationToken);

        if (queueEntry.Status == MobileOrderPostProcessingQueueStatus.RequiresReview)
        {
            _logger.LogError(
                ex,
                "Failed durable post-save processing for mobile sales order {SalesOrderId}. Queue entry {QueueId} moved to {Status}.",
                queueEntry.SalesOrderId,
                queueEntry.Id,
                queueEntry.Status);
        }
        else
        {
            _logger.LogWarning(
                ex,
                "Retryable durable post-save processing failure for mobile sales order {SalesOrderId}. Queue entry {QueueId} moved to {Status}; next retry at {NextRetryAt}.",
                queueEntry.SalesOrderId,
                queueEntry.Id,
                queueEntry.Status,
                queueEntry.NextRetryAt);
        }
    }

    public async Task<SalesOrderDto> UpdateAsync(int id, CreateSalesOrderRequest request, CancellationToken cancellationToken = default)
    {
        await ValidateAndNormalizeSalesOrderRequestAsync(request, cancellationToken);

        var orderIdentity = await _context.SalesOrders
            .AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new { o.Id, o.OrderNumber })
            .FirstOrDefaultAsync(cancellationToken);

        if (orderIdentity == null)
            throw new InvalidOperationException($"Sales order with ID {id} not found");

        await using var postingLock = await AcquireSalesOrderPostingLockAsync(
            orderIdentity.OrderNumber,
            cancellationToken);
        var order = await LoadSalesOrderForPostingAsync(orderIdentity.Id, cancellationToken);

        if (order.Status != SalesOrderStatus.Draft && order.Status != SalesOrderStatus.Pending)
            throw new InvalidOperationException("Only draft or pending orders can be edited");

        // Optimistic concurrency: set the original RowVersion so EF detects conflicts
        if (!string.IsNullOrEmpty(request.RowVersion))
        {
            var originalRowVersion = Convert.FromBase64String(request.RowVersion);
            _context.Entry(order).Property(o => o.RowVersion).OriginalValue = originalRowVersion;
        }

        order.DeliveryDate = request.DeliveryDate.HasValue
            ? DateTime.SpecifyKind(request.DeliveryDate.Value, DateTimeKind.Utc)
            : null;
        order.CardCode = request.CardCode;
        order.CardName = request.CardName;
        order.CustomerRefNo = request.CustomerRefNo;
        order.Comments = request.Comments;
        order.SalesPersonCode = request.SalesPersonCode;
        order.SalesPersonName = request.SalesPersonName;
        order.Currency = request.Currency!;
        order.DiscountPercent = request.DiscountPercent;
        order.ShipToAddress = request.ShipToAddress;
        order.BillToAddress = request.BillToAddress;
        order.WarehouseCode = request.WarehouseCode;
        order.MerchandiserNotes = request.MerchandiserNotes ?? order.MerchandiserNotes;
        order.UpdatedAt = DateTime.UtcNow;

        // The recorded reason describes the order as it was, not as it now is. An edit is how a
        // refused post gets fixed - the unpriced line removed, the quantity corrected - so keeping
        // the old message leaves the list still reporting a failure the order no longer has, which
        // reads as "the fix did not take". The next attempt writes its own reason if it fails.
        order.SyncError = null;

        // Remove existing lines and add new ones
        _context.SalesOrderLines.RemoveRange(order.Lines);
        order.Lines.Clear();

        decimal subTotal = 0;
        decimal taxAmount = 0;
        int lineNum = 0;

        foreach (var lineRequest in request.Lines)
        {
            var taxPercent = ResolveLineTaxPercent(lineRequest.TaxPercent);
            var lineTotal = lineRequest.Quantity * lineRequest.UnitPrice * (1 - lineRequest.DiscountPercent / 100);
            var lineTax = lineTotal * taxPercent / 100;

            var line = new SalesOrderLineEntity
            {
                LineNum = lineNum++,
                ItemCode = lineRequest.ItemCode,
                ItemDescription = lineRequest.ItemDescription,
                Quantity = lineRequest.Quantity,
                UnitPrice = lineRequest.UnitPrice,
                DiscountPercent = lineRequest.DiscountPercent,
                TaxPercent = taxPercent,
                LineTotal = lineTotal,
                WarehouseCode = !string.IsNullOrEmpty(lineRequest.WarehouseCode) ? lineRequest.WarehouseCode : request.WarehouseCode,
                UoMCode = lineRequest.UoMCode,
                BatchNumber = lineRequest.BatchNumber,
                CostCentreCode = lineRequest.CostCentreCode
            };

            order.Lines.Add(line);
            subTotal += lineTotal;
            taxAmount += lineTax;
        }

        order.SubTotal = subTotal;
        order.TaxAmount = taxAmount;
        order.DiscountAmount = subTotal * request.DiscountPercent / 100;
        order.DocTotal = subTotal - order.DiscountAmount + taxAmount;

        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(order);
    }

    public async Task<SalesOrderDto> UpdateStatusAsync(int id, SalesOrderStatus status, Guid userId, string? comments = null, CancellationToken cancellationToken = default)
    {
        var orderIdentity = await _context.SalesOrders
            .AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new { o.Id, o.OrderNumber })
            .FirstOrDefaultAsync(cancellationToken);

        if (orderIdentity == null)
            throw new InvalidOperationException($"Sales order with ID {id} not found");

        await using var postingLock = await AcquireSalesOrderPostingLockAsync(
            orderIdentity.OrderNumber,
            cancellationToken);
        var order = await LoadSalesOrderForPostingAsync(orderIdentity.Id, cancellationToken);

        if (status == SalesOrderStatus.Approved && !HasSapDocNum(order))
            throw new InvalidOperationException("Sales orders can only be marked approved after SAP returns a document number. Use the approve action to post the order to SAP.");

        var previousStatus = order.Status;

        if (!IsValidStatusTransition(previousStatus, status))
            throw new InvalidOperationException($"Cannot change sales order status from {previousStatus} to {status}");

        order.Status = status;
        order.UpdatedAt = DateTime.UtcNow;

        if (status == SalesOrderStatus.Approved)
        {
            order.ApprovedByUserId = userId;
            order.ApprovedDate ??= DateTime.UtcNow;
        }

        if (!string.IsNullOrEmpty(comments))
            order.Comments = (order.Comments ?? "") + $"\n[{DateTime.UtcNow:yyyy-MM-dd HH:mm}] Status changed to {status}: {comments}";

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Updated sales order {OrderId} ({OrderNumber}) status from {PreviousStatus} to {NewStatus} by user {UserId}",
            order.Id,
            order.OrderNumber,
            previousStatus,
            status,
            userId);

        return MapToDto(order);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var orderIdentity = await _context.SalesOrders
            .AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new { o.Id, o.OrderNumber })
            .FirstOrDefaultAsync(cancellationToken);

        if (orderIdentity == null)
            return false;

        await using var postingLock = await AcquireSalesOrderPostingLockAsync(
            orderIdentity.OrderNumber,
            cancellationToken);
        var order = await LoadSalesOrderForPostingAsync(orderIdentity.Id, cancellationToken);

        if (order.Status != SalesOrderStatus.Draft
            && order.Status != SalesOrderStatus.Pending
            && order.Status != SalesOrderStatus.Cancelled
            && order.Status != SalesOrderStatus.Rejected)
        {
            throw new InvalidOperationException("Only draft, pending, cancelled, or rejected orders can be deleted");
        }

        _context.SalesOrders.Remove(order);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<SalesOrderDto> ApproveAsync(int id, Guid userId, CancellationToken cancellationToken = default)
    {
        // A rep is holding the phone waiting for this. The scope covers pricing, UoM resolution
        // and the post itself, so none of those round-trips queue behind background SAP traffic.
        // SapRequestPriorityMiddleware now marks every HTTP request the same way, which makes this
        // a nested no-op on the only route that currently reaches here — kept so the guarantee
        // belongs to the approval itself rather than to the caller happening to be a request.
        using var interactive = SapRequestPriority.BeginInteractive();

        var order = await _context.SalesOrders
            .AsTracking()
            .Include(o => o.Lines)
            .Include(o => o.CreatedByUser)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            throw new InvalidOperationException($"Sales order with ID {id} not found");

        await using var postingLock = await AcquireSalesOrderPostingLockAsync(order.OrderNumber, cancellationToken);
        order = await LoadSalesOrderForPostingAsync(id, cancellationToken);

        if (order.Status == SalesOrderStatus.Approved && HasSapDocNum(order))
        {
            if (!order.IsSynced || !string.IsNullOrWhiteSpace(order.SyncError))
            {
                order.IsSynced = true;
                order.SyncError = null;
                order.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation(
                "Sales order {OrderId} ({OrderNumber}) approval request is idempotent because it is already posted to SAP as DocEntry={DocEntry}, DocNum={DocNum}",
                order.Id,
                order.OrderNumber,
                order.SAPDocEntry,
                order.SAPDocNum);

            return MapToDto(order);
        }

        if (await TryResolveUncertainSapPostingAsync(order, cancellationToken))
        {
            return MapToDto(order);
        }

        if (order.Status == SalesOrderStatus.Approved && !HasSapDocNum(order))
        {
            _logger.LogWarning(
                "Repairing sales order {OrderId} ({OrderNumber}) from Approved to Pending because it has no SAP DocNum.",
                order.Id,
                order.OrderNumber);

            order.Status = SalesOrderStatus.Pending;
            order.ApprovedByUserId = null;
            order.ApprovedDate = null;
        }

        if (!CanApprove(order.Status))
            throw new InvalidOperationException("Only draft or pending orders can be approved");

        var originalStatus = order.Status;
        var originalComments = order.Comments;
        var originalApprovedByUserId = order.ApprovedByUserId;
        var originalApprovedDate = order.ApprovedDate;
        var originalSapDocEntry = order.SAPDocEntry;
        var originalSapDocNum = order.SAPDocNum;
        var originalIsSynced = order.IsSynced;

        // Populate prices from SAP using the BP's price list + any special prices
        await PopulateSAPPricesAsync(order, cancellationToken);

        // Look up approver name
        var approver = await _context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.FirstName, u.LastName, u.Username })
            .FirstOrDefaultAsync(cancellationToken);
        var approverName = approver != null
            ? $"{approver.FirstName} {approver.LastName}".Trim()
            : "Unknown";
        if (string.IsNullOrEmpty(approverName)) approverName = approver?.Username ?? "Unknown";

        // Build approval remarks with origin and approver info
        var catTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time"));
        var createdBy = order.CreatedByUser != null
            ? $"{order.CreatedByUser.FirstName} {order.CreatedByUser.LastName}".Trim()
            : null;
        if (string.IsNullOrEmpty(createdBy)) createdBy = order.CreatedByUser?.Username;

        var approvalRemark = $"Approved by {approverName} on {catTime:dd MMM yyyy HH:mm}. " +
            $"Origin: {order.Source} order{(createdBy != null ? $" created by {createdBy}" : "")}.";

        var (updatedComments, commentsWereTrimmed) = AppendCommentWithinLimit(order.Comments, approvalRemark);
        if (commentsWereTrimmed)
        {
            _logger.LogWarning(
                "Trimmed sales order {OrderId} comments during approval to stay within the {MaxCommentsLength}-character limit.",
                order.Id,
                MaxCommentsLength);
        }

        order.Comments = updatedComments;

        order.ApprovedByUserId = userId;
        order.ApprovedDate = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;

        try
        {
            await PostApprovedOrderToSapAsync(order, cancellationToken, postingLockHeld: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A SAP document may already be linked even though the post reported a failure — the
            // create can commit in SAP and then fail while reading the response or saving locally.
            // Rolling the linkage back would leave the order Pending forever against a live SAP
            // document, so treat a linked order as an approval that succeeded.
            if (HasSapDocNum(order))
            {
                order.Status = SalesOrderStatus.Approved;
                order.IsSynced = true;
                order.SyncError = null;
                order.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync(CancellationToken.None);

                _logger.LogWarning(
                    ex,
                    "Approval of sales order {OrderId} ({OrderNumber}) reported a SAP posting failure but the order is linked to DocEntry={DocEntry}, DocNum={DocNum}. Keeping the approval and the SAP link.",
                    order.Id,
                    order.OrderNumber,
                    order.SAPDocEntry,
                    order.SAPDocNum);

                await _context.Entry(order).Reference(o => o.ApprovedByUser).LoadAsync(CancellationToken.None);

                return MapToDto(order);
            }

            order.Status = originalStatus;
            order.Comments = originalComments;
            order.ApprovedByUserId = originalApprovedByUserId;
            order.ApprovedDate = originalApprovedDate;
            order.SAPDocEntry = originalSapDocEntry;
            order.SAPDocNum = originalSapDocNum;
            order.IsSynced = originalIsSynced;
            order.SyncError = TruncateSyncError(ex.Message);
            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(CancellationToken.None);

            LogFailedSapPost(ex, order, "approve", "Approval state was rolled back.");

            throw;
        }

        _logger.LogInformation(
            "Approved sales order {OrderId} ({OrderNumber}) by user {UserId} and posted it to SAP as DocEntry={DocEntry}, DocNum={DocNum}",
            order.Id,
            order.OrderNumber,
            userId,
            order.SAPDocEntry,
            order.SAPDocNum);

        // The approval and SAP post are already committed above. Use a non-cancellable
        // token for the final navigation load so a client disconnect or request timeout
        // at this last step can't turn an already-successful approval into a thrown
        // exception that gets reported to the caller as a failure.
        await _context.Entry(order).Reference(o => o.ApprovedByUser).LoadAsync(CancellationToken.None);

        return MapToDto(order);
    }

    public async Task<InvoiceDto?> ConvertToInvoiceAsync(int id, Guid userId, CancellationToken cancellationToken = default)
    {
        var order = await _context.SalesOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            throw new InvalidOperationException($"Sales order with ID {id} not found");

        if (order.Status != SalesOrderStatus.Approved)
            throw new InvalidOperationException("Only approved orders can be converted to invoices");

        await EnsureOrderHasValidQuantitiesAsync(order, "conversion to invoice", cancellationToken);

        // Create invoice entity
        var invoice = new InvoiceEntity
        {
            CardCode = order.CardCode,
            CardName = order.CardName,
            DocDate = DateTime.UtcNow,
            DocDueDate = order.DeliveryDate ?? DateTime.UtcNow.AddDays(30),
            DocCurrency = order.Currency,
            DocTotal = order.DocTotal,
            VatSum = order.TaxAmount,
            Comments = $"Created from Sales Order {order.OrderNumber}",
            Status = "Open",
            SalesPersonCode = order.SalesPersonCode
        };

        foreach (var line in order.Lines)
        {
            invoice.DocumentLines.Add(new InvoiceLineEntity
            {
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                DiscountPercent = line.DiscountPercent,
                LineTotal = line.LineTotal,
                WarehouseCode = line.WarehouseCode,
                UoMCode = line.UoMCode
            });
        }

        _context.Invoices.Add(invoice);

        // Update order status
        order.Status = SalesOrderStatus.Fulfilled;
        order.Invoice = invoice;
        order.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Converted sales order {OrderNumber} to invoice {InvoiceId}", order.OrderNumber, invoice.Id);

        return new InvoiceDto
        {
            DocEntry = invoice.Id,
            DocNum = invoice.SAPDocNum ?? invoice.Id,
            DocDate = invoice.DocDate.ToString("yyyy-MM-dd"),
            CardCode = invoice.CardCode,
            CardName = invoice.CardName,
            DocTotal = invoice.DocTotal,
            VatSum = invoice.VatSum,
            DocCurrency = invoice.DocCurrency
        };
    }

    public async Task<SalesOrderDto?> GetByIdFromLocalAsync(int id, CancellationToken cancellationToken = default)
    {
        var order = await _context.SalesOrders
            .Include(o => o.Lines)
            .Include(o => o.Invoice)
            .Include(o => o.CreatedByUser)
            .Include(o => o.ApprovedByUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        return order == null ? null : MapToDto(order);
    }

    public async Task<SalesOrderDto> MarkAsFulfilledAsync(int id, int? invoiceId, CancellationToken cancellationToken = default)
    {
        var order = await _context.SalesOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            throw new InvalidOperationException($"Sales order with ID {id} not found");

        order.Status = SalesOrderStatus.Fulfilled;
        order.InvoiceId = invoiceId;
        order.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Marked sales order {Id} ({OrderNumber}) as Fulfilled, linked to invoice queue",
            order.Id, order.OrderNumber);

        return MapToDto(order);
    }

    private static (DateTime? FromDate, DateTime? ToDate) ResolveSapDateRange(string? cardCode, DateTime? fromDate, DateTime? toDate)
    {
        if (!string.IsNullOrWhiteSpace(cardCode) || fromDate.HasValue || toDate.HasValue)
        {
            return (fromDate, toDate);
        }

        var today = DateTime.UtcNow.Date;
        var startOfMonth = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return (startOfMonth, today);
    }

    private static bool TryMapSapStatusFilter(SalesOrderStatus? status, out string? documentStatus, out string? cancelled)
    {
        documentStatus = null;
        cancelled = null;

        switch (status)
        {
            case null:
                return true;
            case SalesOrderStatus.Approved:
                documentStatus = "bost_Open";
                cancelled = "tNO";
                return true;
            case SalesOrderStatus.Fulfilled:
                documentStatus = "bost_Close";
                cancelled = "tNO";
                return true;
            case SalesOrderStatus.Cancelled:
                cancelled = "tYES";
                return true;
            default:
                return false;
        }
    }

    public async Task<string> GenerateOrderNumberAsync(CancellationToken cancellationToken = default)
    {
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        var prefix = $"SO-{today}-";

        var lastOrder = await _context.SalesOrders
            .Where(o => o.OrderNumber.StartsWith(prefix))
            .OrderByDescending(o => o.OrderNumber.Length)
            .ThenByDescending(o => o.OrderNumber)
            .FirstOrDefaultAsync(cancellationToken);

        var sequence = 1L;
        if (lastOrder != null)
        {
            var lastSequence = lastOrder.OrderNumber.Replace(prefix, "");
            if (long.TryParse(lastSequence, out var parsed))
                sequence = parsed + 1;
        }

        return $"{prefix}{sequence:D4}";
    }

    public async Task<SalesOrderDto> PostToSAPAsync(int id, Guid userId, CancellationToken cancellationToken = default)
    {
        var order = await _context.SalesOrders
            .Include(o => o.Lines)
            .Include(o => o.CreatedByUser)
            .Include(o => o.ApprovedByUser)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            throw new InvalidOperationException($"Sales order with ID {id} not found");

        await using var postingLock = await AcquireSalesOrderPostingLockAsync(order.OrderNumber, cancellationToken);
        order = await LoadSalesOrderForPostingAsync(id, cancellationToken);

        if (order.Status != SalesOrderStatus.Approved)
        {
            _logger.LogWarning(
                "Cannot post sales order {OrderId} ({OrderNumber}) to SAP because current status is {Status}. ApprovedDate={ApprovedDate}, ApprovedByUserId={ApprovedByUserId}",
                order.Id,
                order.OrderNumber,
                order.Status,
                order.ApprovedDate,
                order.ApprovedByUserId);
            throw new InvalidOperationException("Only approved orders can be posted to SAP");
        }

        if (HasSapDocNum(order))
        {
            if (!order.IsSynced || !string.IsNullOrWhiteSpace(order.SyncError))
            {
                order.IsSynced = true;
                order.SyncError = null;
                order.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation(
                "Sales order {OrderId} ({OrderNumber}) post request is idempotent because it is already posted to SAP as DocEntry={DocEntry}, DocNum={DocNum}",
                order.Id,
                order.OrderNumber,
                order.SAPDocEntry,
                order.SAPDocNum);

            return MapToDto(order);
        }

        if (await TryResolveUncertainSapPostingAsync(order, cancellationToken))
        {
            return MapToDto(order);
        }

        try
        {
            await PopulateSAPPricesAsync(order, cancellationToken);
            await PostApprovedOrderToSapAsync(order, cancellationToken, postingLockHeld: true);

            return MapToDto(order);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // As in ApproveAsync: a linked document means the post really did land in SAP, so
            // report success rather than flagging a sync error against a live SAP document.
            if (HasSapDocNum(order))
            {
                order.Status = SalesOrderStatus.Approved;
                order.IsSynced = true;
                order.SyncError = null;
                order.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(CancellationToken.None);

                _logger.LogWarning(
                    ex,
                    "Posting sales order {OrderNumber} to SAP reported a failure but the order is linked to DocEntry={DocEntry}, DocNum={DocNum}. Keeping the SAP link.",
                    order.OrderNumber,
                    order.SAPDocEntry,
                    order.SAPDocNum);

                return MapToDto(order);
            }

            if (order.Status == SalesOrderStatus.Approved)
            {
                order.Status = SalesOrderStatus.Pending;
                order.ApprovedByUserId = null;
                order.ApprovedDate = null;
            }

            order.SyncError = TruncateSyncError(ex.Message);
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(CancellationToken.None);

            LogFailedSapPost(ex, order, "post", "The order was returned to Pending.");
            throw;
        }
    }

    private async Task<SalesOrderEntity> LoadSalesOrderForPostingAsync(int id, CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();

        return await _context.SalesOrders
            .AsTracking()
            .Include(o => o.Lines)
            .Include(o => o.CreatedByUser)
            .Include(o => o.ApprovedByUser)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Sales order with ID {id} not found");
    }

    private async Task PostApprovedOrderToSapAsync(
        SalesOrderEntity order,
        CancellationToken cancellationToken,
        bool postingLockHeld = false)
    {
        if (postingLockHeld)
        {
            await PostApprovedOrderToSapCoreAsync(order, cancellationToken);
            return;
        }

        await using var postingLock = await AcquireSalesOrderPostingLockAsync(order.OrderNumber, cancellationToken);
        await PostApprovedOrderToSapCoreAsync(order, cancellationToken);
    }

    private async Task PostApprovedOrderToSapCoreAsync(SalesOrderEntity order, CancellationToken cancellationToken)
    {
        if (!CanPostToSap(order.Status))
        {
            throw new InvalidOperationException(
                $"Sales order {order.OrderNumber} cannot be posted to SAP while its current status is {order.Status}.");
        }

        await EnsureOrderHasValidQuantitiesAsync(order, "posting to SAP", cancellationToken);

        if (HasSapDocNum(order))
        {
            if (!order.IsSynced || !string.IsNullOrWhiteSpace(order.SyncError))
            {
                order.IsSynced = true;
                order.SyncError = null;
                order.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation(
                "Skipped SAP sales order create for {OrderNumber} because the local order is already linked to DocEntry={DocEntry}, DocNum={DocNum}",
                order.OrderNumber,
                order.SAPDocEntry,
                order.SAPDocNum);

            return;
        }

        // Runs before the credit gate because an unpriced line understates DocTotal, so credit
        // would wave the order through on a total that is not the real one.
        EnsureOrderLinesArePriced(order);

        // The authoritative gate: every route into SAP passes here, and by this point the order
        // carries its real prices — a mobile order is priced after capture, so this is the first
        // moment its true value is known. It sits after the already-linked check on purpose, so
        // reconciling an order SAP has already accepted cannot be refused on credit.
        await EnsureWithinCreditLimitAsync(order.CardCode, order.DocTotal, cancellationToken);

        var sapPostingMarkerRequest = new { orderNumber = order.OrderNumber };
        long? idempotencyRequestId = null;
        var releaseIdempotencyRequest = false;
        var sapCreateReturned = false;
        var staleMarkerTakeoverAttempted = false;

        try
        {
            while (!idempotencyRequestId.HasValue)
            {
                var acquireResult = await _idempotencyRequestStore.TryAcquireAsync<SAPSalesOrder>(
                    SapPostingIdempotencyScope,
                    order.OrderNumber,
                    sapPostingMarkerRequest,
                    cancellationToken);

                switch (acquireResult.Outcome)
                {
                    case IdempotencyAcquireOutcome.ReplayAvailable when acquireResult.Response is not null:
                        EnsureOrderAttached(order);
                        ApplySapDocumentToLocalOrder(order, acquireResult.Response);
                        await TryRefreshLocalSalesOrderSnapshotFromSapAsync(order, CancellationToken.None);
                        await _context.SaveChangesAsync(CancellationToken.None);

                        _logger.LogInformation(
                            "Replayed SAP sales order post for {OrderNumber} as DocEntry={DocEntry}, DocNum={DocNum} from the durable idempotency marker.",
                            order.OrderNumber,
                            acquireResult.Response.DocEntry,
                            acquireResult.Response.DocNum);
                        return;

                    case IdempotencyAcquireOutcome.InProgress:
                        if (await TryAttachExistingSapSalesOrderWithRetryAsync(
                                order,
                                "concurrent SAP posting marker reconciliation",
                                CancellationToken.None))
                        {
                            return;
                        }

                        var markerCreatedAt = acquireResult.CreatedAtUtc.GetValueOrDefault(DateTime.UtcNow);
                        var markerRetryAt = markerCreatedAt.Add(SapPostingUncertainRetryHold);
                        var markerIsStale = markerRetryAt <= DateTime.UtcNow;

                        if (markerIsStale
                            && !staleMarkerTakeoverAttempted
                            && acquireResult.RequestId.HasValue)
                        {
                            staleMarkerTakeoverAttempted = true;
                            await _idempotencyRequestStore.ReleaseAsync(
                                acquireResult.RequestId.Value,
                                CancellationToken.None);

                            _logger.LogWarning(
                                "Released abandoned SAP sales order posting marker {RequestId} for {OrderNumber}, created at {CreatedAtUtc:O}, after SAP reconciliation found no document. Retrying under a new marker.",
                                acquireResult.RequestId.Value,
                                order.OrderNumber,
                                markerCreatedAt);
                            continue;
                        }

                        var waitMinutes = Math.Max(
                            1,
                            (int)Math.Ceiling((markerRetryAt - DateTime.UtcNow).TotalMinutes));
                        throw new InvalidOperationException(
                            $"Sales order {order.OrderNumber} has an unfinished SAP posting attempt. No SAP document was found yet; retry in {waitMinutes} minute(s).");

                    case IdempotencyAcquireOutcome.RequestMismatch:
                        throw new InvalidOperationException(
                            $"Sales order {order.OrderNumber} is already protected by a different SAP posting request. Please refresh the order before trying again.");

                    case IdempotencyAcquireOutcome.Acquired:
                        idempotencyRequestId = acquireResult.RequestId
                            ?? throw new InvalidOperationException("The SAP posting marker was acquired without a request ID.");
                        releaseIdempotencyRequest = true;
                        break;
                }
            }

            if (await TryAttachExistingSapSalesOrderAsync(order, "pre-create duplicate check", cancellationToken))
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // TryAttachExistingSapSalesOrderAsync just ran the same U_OrderNumber probe above, under
            // this order's posting lock, so the client does not need to repeat it.
            var sapOrder = await _sapClient.CreateSalesOrderAsync(
                order,
                CancellationToken.None,
                duplicateCheckAlreadyPerformed: true);
            sapCreateReturned = true;

            if (sapOrder.DocNum <= 0)
                throw new InvalidOperationException("SAP created the sales order but did not return a document number.");

            ApplySapDocumentToLocalOrder(order, sapOrder);

            await TryRefreshLocalSalesOrderSnapshotFromSapAsync(order, CancellationToken.None);

            await SaveSapPostingResultAsync(order, sapOrder);

            if (idempotencyRequestId.HasValue)
            {
                try
                {
                    await _idempotencyRequestStore.CompleteAsync(
                        idempotencyRequestId.Value,
                        sapOrder,
                        CancellationToken.None);
                    releaseIdempotencyRequest = false;
                }
                catch (Exception completeException)
                {
                    _logger.LogWarning(
                        completeException,
                        "Failed to persist SAP sales order posting idempotency completion for {OrderNumber} as request {RequestId}.",
                        order.OrderNumber,
                        idempotencyRequestId.Value);
                }
            }

            _logger.LogInformation(
                "Posted sales order {OrderNumber} to SAP as DocEntry={DocEntry}, DocNum={DocNum}",
                order.OrderNumber,
                sapOrder.DocEntry,
                sapOrder.DocNum);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "SAP sales order create for {OrderNumber} did not complete cleanly. Checking SAP for an already-created document before allowing a retry.",
                order.OrderNumber);

            if (await TryAttachExistingSapSalesOrderWithRetryAsync(
                    order,
                    "post-create failure reconciliation",
                    CancellationToken.None))
            {
                return;
            }

            if (sapCreateReturned || IsPotentiallyUncertainSapCreateFailure(ex))
            {
                throw new InvalidOperationException(BuildUncertainSapPostingMessage(order.OrderNumber), ex);
            }

            throw;
        }
        finally
        {
            if (releaseIdempotencyRequest && idempotencyRequestId.HasValue)
            {
                try
                {
                    await _idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None);
                }
                catch (Exception releaseException)
                {
                    _logger.LogWarning(
                        releaseException,
                        "Failed to release SAP sales order posting idempotency request {RequestId} for {OrderNumber}.",
                        idempotencyRequestId.Value,
                        order.OrderNumber);
                }
            }
        }
    }

    private async Task<bool> TryResolveUncertainSapPostingAsync(
        SalesOrderEntity order,
        CancellationToken cancellationToken)
    {
        if (!IsSapPostingUncertain(order))
        {
            return false;
        }

        if (await TryAttachExistingSapSalesOrderWithRetryAsync(
                order,
                "uncertain previous posting reconciliation",
                cancellationToken))
        {
            return true;
        }

        var retryBlockedUntil = order.UpdatedAt.Add(SapPostingUncertainRetryHold);
        var now = DateTime.UtcNow;
        if (retryBlockedUntil > now)
        {
            var waitMinutes = Math.Max(1, (int)Math.Ceiling((retryBlockedUntil - now).TotalMinutes));
            throw new InvalidOperationException(
                $"{SapPostingUncertainSyncErrorPrefix} for sales order {order.OrderNumber}. SAP has not returned the document by U_OrderNumber yet, so automatic reposting is blocked for another {waitMinutes} minute(s) to prevent a duplicate.");
        }

        _logger.LogWarning(
            "Clearing uncertain SAP posting marker for sales order {OrderId} ({OrderNumber}) after {HoldMinutes} minutes because no SAP document was found by U_OrderNumber. A new post attempt will be allowed.",
            order.Id,
            order.OrderNumber,
            SapPostingUncertainRetryHold.TotalMinutes);

        order.SyncError = null;
        order.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return false;
    }

    private async Task<bool> TryAttachExistingSapSalesOrderAsync(
        SalesOrderEntity order,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(order.OrderNumber))
        {
            return false;
        }

        var existingSapOrder = await _sapClient.GetSalesOrderByOrderNumberAsync(order.OrderNumber, cancellationToken);
        if (existingSapOrder is null || existingSapOrder.DocNum <= 0)
        {
            return false;
        }

        ApplySapDocumentToLocalOrder(order, existingSapOrder);
        await TryRefreshLocalSalesOrderSnapshotFromSapAsync(order, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogWarning(
            "Linked local sales order {OrderId} ({OrderNumber}) to existing SAP sales order DocEntry={DocEntry}, DocNum={DocNum} instead of creating a duplicate. Reason: {Reason}",
            order.Id,
            order.OrderNumber,
            existingSapOrder.DocEntry,
            existingSapOrder.DocNum,
            reason);

        return true;
    }

    private async Task<bool> TryAttachExistingSapSalesOrderWithRetryAsync(
        SalesOrderEntity order,
        string reason,
        CancellationToken cancellationToken)
    {
        // SAP can take appreciably longer than a few seconds to make a freshly committed document
        // visible to the OData query, especially for large multi-line orders. The old window was
        // 5 attempts over ~7.5s, which gave up while the document was still landing and left the
        // order stranded as Pending. Capped exponential backoff widens this to roughly 25s of
        // delay. It is deliberately not longer: this runs inside the request while holding the
        // posting lock, and SalesOrderReconciliationJob sweeps anything that takes longer.
        const int maxAttempts = 6;
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(8);
        var startedAt = DateTime.UtcNow;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (await TryAttachExistingSapSalesOrderAsync(order, reason, cancellationToken))
            {
                return true;
            }

            if (attempt == maxAttempts)
            {
                break;
            }

            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
        }

        _logger.LogWarning(
            "No existing SAP sales order with U_OrderNumber {OrderNumber} was found after {AttemptCount} reconciliation attempts over {ElapsedSeconds:F0}s. The background reconciliation sweep will keep retrying.",
            order.OrderNumber,
            maxAttempts,
            (DateTime.UtcNow - startedAt).TotalSeconds);

        return false;
    }

    /// <summary>
    /// Sweeps recent local sales orders whose posting to SAP was attempted but left no SAP DocNum,
    /// and links any that SAP actually holds under their U_OrderNumber. A create can commit in SAP
    /// while the response, the local save, or the short post-failure reconciliation window fails,
    /// which leaves the local row showing Pending forever because nothing else re-checks it once
    /// the request has ended.
    /// </summary>
    /// <remarks>
    /// "Posting was attempted" is the load-bearing part of the candidate filter — an order nobody
    /// ever submitted cannot be in SAP, so probing it is pure cost. See
    /// <see cref="UnlinkedSapOrderCandidateFilter"/>.
    /// </remarks>
    public async Task<int> ReconcileUnlinkedSapSalesOrdersAsync(
        TimeSpan lookback,
        int maxOrders,
        CancellationToken cancellationToken = default)
    {
        if (maxOrders <= 0)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - lookback;

        var candidates = await _context.SalesOrders
            .AsNoTracking()
            .Where(UnlinkedSapOrderCandidateFilter(cutoff))
            .OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.OrderNumber, o.CreatedAt })
            .Take(maxOrders)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        // A full batch means orders beyond the cap were not looked at this sweep, and because the
        // ordering is oldest-first the ones being skipped are the newest — the opposite of what a
        // repair sweep wants. Worth saying out loud: reaching the cap at all implies a backlog of
        // orders whose posting is genuinely uncertain, which is an operator problem, not a tuning one.
        if (candidates.Count >= maxOrders)
        {
            _logger.LogWarning(
                "Sales order reconciliation is at its {MaxOrders}-order cap, so newer unlinked orders were not probed this sweep.",
                maxOrders);
        }

        // One scan of ORDR for the whole sweep. This used to probe U_OrderNumber once per
        // candidate, and since a candidate only stops being one when it links, an order SAP never
        // received stayed in the set and was re-scanned every two minutes for the whole lookback
        // window — thousands of unindexed scans for a single stuck order.
        IReadOnlyDictionary<string, SAPSalesOrder> sapOrdersByNumber;
        try
        {
            sapOrdersByNumber = await _sapClient.GetSalesOrdersByOrderNumbersAsync(
                candidates.Select(candidate => candidate.OrderNumber),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve {CandidateCount} unlinked sales order(s) against SAP by U_OrderNumber. They will be retried on the next sweep.",
                candidates.Count);
            return 0;
        }

        var linkedCount = 0;

        // Only the orders SAP actually holds are worth a posting lock and a re-read; the rest cost
        // nothing beyond the single probe above.
        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!sapOrdersByNumber.TryGetValue(candidate.OrderNumber, out var sapOrder))
            {
                continue;
            }

            try
            {
                if (await TryReconcileUnlinkedSapSalesOrderAsync(candidate.Id, sapOrder, cancellationToken))
                {
                    linkedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to reconcile sales order {OrderId} against SAP by U_OrderNumber. It will be retried on the next sweep.",
                    candidate.Id);
            }
        }

        if (linkedCount > 0)
        {
            _logger.LogInformation(
                "Linked {LinkedCount} of {CandidateCount} unlinked local sales order(s) to SAP documents found by U_OrderNumber.",
                linkedCount,
                candidates.Count);
        }
        else
        {
            // Name them. A sweep that links nothing is normal, but a sweep that links nothing every
            // two minutes for hours means those orders need a person, and the previous version of
            // this said only "Resolved 0 of 22" from inside the SAP client — enough to know
            // something was stuck, not enough to know what.
            //
            // Past StuckCandidateAge it stops being a sweep waiting for SAP to catch up and becomes
            // a report that somebody has to act on, so it is raised to Warning: SAP has now been
            // asked repeatedly, over hours, and has answered the same way every time. The
            // reconciliation job also drops these to a half-hourly cadence, so this names a genuinely
            // stuck order roughly 48 times a day rather than 720.
            var stuckSince = DateTime.UtcNow - StuckCandidateAge;
            var stuck = candidates.Where(candidate => candidate.CreatedAt <= stuckSince).ToList();

            if (stuck.Count > 0)
            {
                _logger.LogWarning(
                    "Sales order reconciliation has found no SAP document for {StuckCount} order(s) created more than {StuckHours:F0}h ago; they will not resolve on their own and need a person: {OrderNumbers}.",
                    stuck.Count,
                    StuckCandidateAge.TotalHours,
                    NameOrders(stuck.Select(candidate => candidate.OrderNumber)));
            }

            if (stuck.Count < candidates.Count)
            {
                _logger.LogInformation(
                    "Sales order reconciliation linked none of its {CandidateCount} candidate(s); SAP holds no document under {OrderNumbers}.",
                    candidates.Count - stuck.Count,
                    NameOrders(candidates
                        .Where(candidate => candidate.CreatedAt > stuckSince)
                        .Select(candidate => candidate.OrderNumber)));
            }
        }

        return linkedCount;
    }

    /// <summary>
    /// Names up to <see cref="MaxReportedStuckOrderNumbers"/> orders, counting the rest.
    /// </summary>
    private static string NameOrders(IEnumerable<string> orderNumbers)
    {
        var names = orderNumbers.ToList();
        var shown = string.Join(", ", names.Take(MaxReportedStuckOrderNumbers));

        return names.Count > MaxReportedStuckOrderNumbers
            ? $"{shown}, +{names.Count - MaxReportedStuckOrderNumbers} more"
            : shown;
    }

    /// <summary>
    /// Links one local order to the SAP document already resolved for it, under the posting lock.
    /// </summary>
    private async Task<bool> TryReconcileUnlinkedSapSalesOrderAsync(
        int orderId,
        SAPSalesOrder sapOrder,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();

        var order = await _context.SalesOrders
            .AsTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order is null || HasSapDocNum(order))
        {
            return false;
        }

        IAsyncDisposable postingLock;
        try
        {
            // TimeSpan.Zero: never make a user's approve request queue behind this sweep.
            postingLock = await AcquireSalesOrderPostingLockAsync(
                order.OrderNumber,
                cancellationToken,
                maxWait: TimeSpan.Zero);
        }
        catch (InvalidOperationException)
        {
            // A live approve/post request already owns this order. Leave it to that request.
            _logger.LogDebug(
                "Skipped SAP reconciliation for sales order {OrderId} ({OrderNumber}) because a posting attempt holds the lock.",
                order.Id,
                order.OrderNumber);
            return false;
        }

        await using (postingLock)
        {
            // Re-read under the lock: the holder may have linked the document while we waited.
            _context.ChangeTracker.Clear();
            order = await _context.SalesOrders
                .AsTracking()
                .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

            if (order is null || HasSapDocNum(order))
            {
                return false;
            }

            // The document was resolved before the lock was taken. Re-probing here would put the
            // per-order ORDR scan straight back, which is what the batched sweep exists to avoid.
            ApplySapDocumentToLocalOrder(order, sapOrder);
            await TryRefreshLocalSalesOrderSnapshotFromSapAsync(order, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Linked local sales order {OrderId} ({OrderNumber}) to existing SAP sales order DocEntry={DocEntry}, DocNum={DocNum} found by background reconciliation of an unlinked local order.",
                order.Id,
                order.OrderNumber,
                sapOrder.DocEntry,
                sapOrder.DocNum);

            return true;
        }
    }

    private static bool IsSapPostingUncertain(SalesOrderEntity order)
        => !HasSapDocNum(order)
           && !string.IsNullOrWhiteSpace(order.SyncError)
           && order.SyncError.StartsWith(SapPostingUncertainSyncErrorPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsPotentiallyUncertainSapCreateFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException
                || current is TimeoutException
                || current is System.Net.Http.HttpRequestException)
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildUncertainSapPostingMessage(string orderNumber)
        => $"{SapPostingUncertainSyncErrorPrefix} for sales order {orderNumber}. The app could not confirm whether SAP accepted the create request, so duplicate prevention has blocked automatic reposting until SAP can be checked by U_OrderNumber.";

    private static void ApplySapDocumentToLocalOrder(SalesOrderEntity order, SAPSalesOrder sapOrder)
    {
        order.Status = SalesOrderStatus.Approved;
        order.SAPDocEntry = sapOrder.DocEntry > 0 ? sapOrder.DocEntry : order.SAPDocEntry;
        order.SAPDocNum = sapOrder.DocNum;
        order.IsSynced = true;
        order.SyncError = null;
        order.UpdatedAt = DateTime.UtcNow;
    }

    private async Task SaveSapPostingResultAsync(SalesOrderEntity order, SAPSalesOrder sapOrder)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _context.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < maxAttempts)
            {
                if (ex.Entries.Count == 0 || ex.Entries.Any(entry => entry.State != EntityState.Modified))
                {
                    throw;
                }

                foreach (var entry in ex.Entries)
                {
                    var proposedValues = entry.CurrentValues.Clone();
                    var databaseValues = await entry.GetDatabaseValuesAsync(CancellationToken.None);
                    if (databaseValues is null)
                    {
                        throw new InvalidOperationException(
                            $"Sales order {order.OrderNumber} or one of its lines was deleted while SAP document {sapOrder.DocNum} was being saved.",
                            ex);
                    }

                    entry.OriginalValues.SetValues(databaseValues);
                    entry.CurrentValues.SetValues(proposedValues);
                }

                _logger.LogWarning(
                    "Sales order {OrderId} ({OrderNumber}) had {ConflictCount} concurrent row change(s) while SAP document {SapDocNum} was being linked locally. Retrying with current database row versions ({Attempt}/{MaxAttempts}).",
                    order.Id,
                    order.OrderNumber,
                    ex.Entries.Count,
                    sapOrder.DocNum,
                    attempt,
                    maxAttempts);
            }
        }

        throw new InvalidOperationException(
            $"Sales order {order.OrderNumber} could not be linked to SAP document {sapOrder.DocNum} after {maxAttempts} attempts.");
    }

    private void EnsureOrderAttached(SalesOrderEntity order)
    {
        if (_context.Entry(order).State != EntityState.Detached)
        {
            return;
        }

        _context.SalesOrders.Attach(order);
    }

    /// <summary>
    /// Re-reads each posted order's SAP document and rewrites the local financial mirror from it,
    /// including the per-line tax rate the local rows never carried.
    /// </summary>
    /// <remarks>
    /// The header numbers come straight from SAP because SAP is the only place they are true: it
    /// prices the tax from each item's own tax code, so a local recompute at the configured VAT
    /// rate would be right only for an order whose lines are all standard-rated. The per-line rate
    /// is then derived from those same header numbers rather than from configuration, so the lines
    /// can never sum to something the header contradicts. An order SAP prices at zero tax keeps
    /// zero-rate lines, which is the correct answer rather than a missing one.
    /// </remarks>
    public async Task<SalesOrderTaxRepairSummary> RepairSyncedSalesOrderTaxFromSapAsync(
        IReadOnlyCollection<int> orderIds,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var normalizedOrderIds = orderIds.Distinct().ToList();
        if (normalizedOrderIds.Count == 0)
            return new SalesOrderTaxRepairSummary(0, 0, 0);

        var orders = await _context.SalesOrders
            .Include(order => order.Lines)
            .Where(order => normalizedOrderIds.Contains(order.Id)
                && order.IsSynced
                && order.SAPDocEntry.HasValue
                && order.SAPDocEntry.Value > 0)
            .ToListAsync(cancellationToken);

        var ordersRepaired = 0;
        var linesRepaired = 0;
        var unresolved = 0;

        foreach (var order in orders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SAPSalesOrder? sapOrder;
            try
            {
                sapOrder = await _sapClient.GetSalesOrderByDocEntryAsync(
                    order.SAPDocEntry.GetValueOrDefault(),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not read SAP document {DocEntry} for sales order {OrderNumber} while repairing its tax mirror.",
                    order.SAPDocEntry,
                    order.OrderNumber);
                unresolved++;
                continue;
            }

            if (sapOrder == null)
            {
                _logger.LogWarning(
                    "SAP holds no document under DocEntry {DocEntry} for sales order {OrderNumber}; leaving its tax mirror untouched.",
                    order.SAPDocEntry,
                    order.OrderNumber);
                unresolved++;
                continue;
            }

            var headerChanged = ApplySapFinancialSnapshotToLocalOrder(order, sapOrder);
            var orderLinesRepaired = ApplyEffectiveTaxPercentToZeroRatedLines(order);

            if (!headerChanged && orderLinesRepaired == 0)
                continue;

            ordersRepaired++;
            linesRepaired += orderLinesRepaired;
        }

        if (dryRun)
        {
            // Nothing is written, so the tracked edits must not survive into whatever else this
            // scoped context is asked to save.
            _context.ChangeTracker.Clear();
        }
        else if (ordersRepaired > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        return new SalesOrderTaxRepairSummary(ordersRepaired, linesRepaired, unresolved);
    }

    /// <summary>
    /// Spreads the order's own effective tax rate across the lines that carry none, leaving any
    /// line that already has a rate alone.
    /// </summary>
    private static int ApplyEffectiveTaxPercentToZeroRatedLines(SalesOrderEntity order)
    {
        if (order.Lines == null || order.Lines.Count == 0)
            return 0;

        if (order.SubTotal <= 0 || order.TaxAmount <= 0)
            return 0;

        var effectiveTaxPercent = Math.Round(order.TaxAmount / order.SubTotal * 100m, 3, MidpointRounding.AwayFromZero);
        if (effectiveTaxPercent <= 0)
            return 0;

        var linesRepaired = 0;

        foreach (var line in order.Lines)
        {
            if (line.TaxPercent > 0)
                continue;

            line.TaxPercent = effectiveTaxPercent;
            linesRepaired++;
        }

        return linesRepaired;
    }

    private async Task RefreshLocalSalesOrderSnapshotsFromSapAsync(
        IEnumerable<int> orderIds,
        CancellationToken cancellationToken)
    {
        var normalizedOrderIds = orderIds
            .Distinct()
            .ToList();

        if (normalizedOrderIds.Count == 0)
            return;

        var syncedOrders = await _context.SalesOrders
            .Where(order => normalizedOrderIds.Contains(order.Id)
                && order.IsSynced
                && order.SAPDocEntry.HasValue
                && order.SAPDocEntry.Value > 0)
            .ToListAsync(cancellationToken);

        var updatedOrderCount = 0;

        foreach (var order in syncedOrders)
        {
            if (await TryRefreshLocalSalesOrderSnapshotFromSapAsync(order, cancellationToken))
            {
                updatedOrderCount++;
            }
        }

        if (updatedOrderCount == 0)
            return;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Refreshed SAP financial snapshots for {OrderCount} local sales order(s) while serving local filtered results.",
            updatedOrderCount);
    }

    private async Task<bool> TryRefreshLocalSalesOrderSnapshotFromSapAsync(
        SalesOrderEntity order,
        CancellationToken cancellationToken)
    {
        var sapDocEntry = order.SAPDocEntry.GetValueOrDefault();
        if (sapDocEntry <= 0)
            return false;

        try
        {
            var sapOrder = await _sapClient.GetSalesOrderByDocEntryAsync(sapDocEntry, cancellationToken);
            if (sapOrder == null)
                return false;

            return ApplySapFinancialSnapshotToLocalOrder(order, sapOrder);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to refresh local SAP financial snapshot for sales order {OrderId} ({OrderNumber}) with SAP DocEntry={DocEntry}.",
                order.Id,
                order.OrderNumber,
                order.SAPDocEntry);

            return false;
        }
    }

    private static bool ApplySapFinancialSnapshotToLocalOrder(SalesOrderEntity order, SAPSalesOrder sapOrder)
    {
        var hasChanges = false;

        if (sapOrder.DocEntry > 0 && order.SAPDocEntry != sapOrder.DocEntry)
        {
            order.SAPDocEntry = sapOrder.DocEntry;
            hasChanges = true;
        }

        if (sapOrder.DocNum > 0 && order.SAPDocNum != sapOrder.DocNum)
        {
            order.SAPDocNum = sapOrder.DocNum;
            hasChanges = true;
        }

        if (!order.IsSynced)
        {
            order.IsSynced = true;
            hasChanges = true;
        }

        if (!string.IsNullOrWhiteSpace(order.SyncError))
        {
            order.SyncError = null;
            hasChanges = true;
        }

        if (!string.IsNullOrWhiteSpace(sapOrder.DocCurrency)
            && !string.Equals(order.Currency, sapOrder.DocCurrency, StringComparison.OrdinalIgnoreCase))
        {
            order.Currency = sapOrder.DocCurrency;
            hasChanges = true;
        }

        var sapDocTotal = sapOrder.DocTotal ?? order.DocTotal;
        var sapTaxAmount = sapOrder.VatSum ?? order.TaxAmount;
        var sapSubTotal = sapOrder.DocTotal.HasValue || sapOrder.VatSum.HasValue
            ? Math.Max(sapDocTotal - sapTaxAmount, 0m)
            : order.SubTotal;
        var sapDiscountPercent = sapOrder.DiscountPercent ?? order.DiscountPercent;
        var sapDiscountAmount = sapOrder.TotalDiscount ?? order.DiscountAmount;

        if (order.SubTotal != sapSubTotal)
        {
            order.SubTotal = sapSubTotal;
            hasChanges = true;
        }

        if (order.TaxAmount != sapTaxAmount)
        {
            order.TaxAmount = sapTaxAmount;
            hasChanges = true;
        }

        if (order.DiscountPercent != sapDiscountPercent)
        {
            order.DiscountPercent = sapDiscountPercent;
            hasChanges = true;
        }

        if (order.DiscountAmount != sapDiscountAmount)
        {
            order.DiscountAmount = sapDiscountAmount;
            hasChanges = true;
        }

        if (order.DocTotal != sapDocTotal)
        {
            order.DocTotal = sapDocTotal;
            hasChanges = true;
        }

        if (hasChanges)
        {
            order.UpdatedAt = DateTime.UtcNow;
        }

        return hasChanges;
    }

    private static string TruncateSyncError(string message)
        => message.Length > 500 ? message[..500] : message;

    private static bool HasSapDocNum(SalesOrderEntity order)
        => order.SAPDocNum.GetValueOrDefault() > 0;

    /// <summary>
    /// Takes the per-order SAP posting advisory lock.
    /// </summary>
    /// <param name="orderNumber">Local order number the lock is scoped to.</param>
    /// <param name="cancellationToken">Cancels the wait and the underlying database calls.</param>
    /// <param name="maxWait">
    /// How long to keep retrying before giving up. User-facing requests wait briefly so that a
    /// short-lived holder — notably <see cref="SalesOrderReconciliationJob"/>, which takes this
    /// same lock per order — does not surface as "another request is approving this order" to
    /// somebody clicking Approve. Kept short on purpose: a genuine concurrent approve runs far
    /// longer than this, so real contention still reports honestly instead of being serialised
    /// silently. Pass <see cref="TimeSpan.Zero"/> for background callers that must yield
    /// immediately to user requests rather than queue behind them.
    /// </param>
    private async Task<IAsyncDisposable> AcquireSalesOrderPostingLockAsync(
        string orderNumber,
        CancellationToken cancellationToken,
        TimeSpan? maxWait = null)
    {
        if (string.IsNullOrWhiteSpace(orderNumber))
        {
            throw new InvalidOperationException("Sales order posting requires an order number.");
        }

        var lockName = $"sales-order-post:{orderNumber.Trim().ToUpperInvariant()}";
        var advisoryKey = ComputeAdvisoryKey(lockName);
        var waitBudget = maxWait ?? PostingLockAcquireWait;
        var pollInterval = TimeSpan.FromMilliseconds(250);
        var deadline = DateTime.UtcNow + waitBudget;
        NpgsqlConnection? connection = null;

        try
        {
            connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var attempt = 0;
            while (true)
            {
                attempt++;

                await using (var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection))
                {
                    command.Parameters.AddWithValue("key", advisoryKey);
                    var acquired = await command.ExecuteScalarAsync(cancellationToken) as bool? == true;
                    if (acquired)
                    {
                        break;
                    }
                }

                if (DateTime.UtcNow >= deadline)
                {
                    if (waitBudget == TimeSpan.Zero)
                    {
                        _logger.LogDebug(
                            "Background SAP reconciliation yielded posting lock for {OrderNumber}; another holder owns it",
                            orderNumber);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Could not acquire the SAP posting lock for {OrderNumber} after {Attempts} attempt(s) over {WaitSeconds:F1}s; another holder still owns it.",
                            orderNumber,
                            attempt,
                            waitBudget.TotalSeconds);
                    }

                    throw new InvalidOperationException(
                        $"Sales order {orderNumber} is already being approved and posted to SAP by another request. Wait for it to finish, then refresh the order before trying again.");
                }

                await Task.Delay(pollInterval, cancellationToken);
            }

            _logger.LogDebug(
                "Acquired sales order SAP posting lock for {OrderNumber} after {Attempts} attempt(s)",
                orderNumber,
                attempt);

            var handle = new SalesOrderPostingLockHandle(orderNumber, advisoryKey, connection, _logger);
            connection = null;
            return handle;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private static long ComputeAdvisoryKey(string lockName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(lockName));
        return BitConverter.ToInt64(hash, 0);
    }

    private sealed class SalesOrderPostingLockHandle(
        string orderNumber,
        long advisoryKey,
        NpgsqlConnection connection,
        ILogger<SalesOrderService> logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                    command.Parameters.AddWithValue("key", advisoryKey);
                    await command.ExecuteScalarAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release sales order SAP posting lock for {OrderNumber}", orderNumber);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }

    private static string TruncateMobileQueueError(string message)
        => message.Length > 2000 ? message[..2000] : message;

    private async Task TryRollbackCreateTransactionAsync(
        IDbContextTransaction transaction,
        Exception? originalException = null)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception rollbackException)
        {
            _logger.LogWarning(
                rollbackException,
                "Failed to roll back a sales-order creation transaction after {OriginalExceptionType}; preserving the original failure",
                originalException?.GetType().Name ?? "a database update failure");
        }
    }

    internal static bool CanPostToSap(SalesOrderStatus status) =>
        status is SalesOrderStatus.Draft
            or SalesOrderStatus.Pending
            or SalesOrderStatus.Approved;

    /// <summary>
    /// The statuses an order can still be approved from, and so the statuses a list must offer the
    /// action for.
    /// </summary>
    /// <remarks>
    /// Draft is not a resting state. A Web order is created as Draft and auto-posted in the same
    /// call; when that post is refused the create path puts it back to Draft with the reason
    /// recorded, so Draft is precisely where an order that needs approving again sits. A list that
    /// offers approval on Pending alone strands every one of those orders: the cause can be edited
    /// away but nothing is left that will retry the post.
    /// </remarks>
    internal static bool CanApprove(SalesOrderStatus status) =>
        status is SalesOrderStatus.Draft or SalesOrderStatus.Pending;

    /// <summary>
    /// Selects local orders created since <paramref name="cutoff"/> that were submitted to SAP but
    /// carry no SAP document number — the only orders a reconciliation sweep can possibly link.
    /// </summary>
    /// <remarks>
    /// Null *or* non-positive DocNum: everywhere else "has a SAP document" means
    /// <c>HasSapDocNum</c>, which reads DocNum &lt;= 0 as unlinked. Matching only null left those
    /// rows to the repair loop that used to run on the mobile order list, and the sweep is now the
    /// only thing that relinks them.
    ///
    /// Status alone cannot say whether SAP was ever asked to create the document, and only an order
    /// that was asked can be sitting in SAP unlinked:
    /// <list type="bullet">
    /// <item><description><c>Pending</c>/<c>Draft</c> with a <c>SyncError</c> — the live limbo
    /// state. A failed or uncertain post rolls the status back to what it was and records the
    /// message, so the SyncError is the record that SAP was asked at all. No SyncError means
    /// nothing was ever posted.</description></item>
    /// <item><description><c>Approved</c> with no DocNum — the same limbo, but only reachable on
    /// rows written before <c>ApplicationDbContext.EnsureApprovedSalesOrdersHaveSapDocNum</c>
    /// started rejecting that combination on save. Kept so those rows still get repaired; it is not
    /// where new cases arrive.</description></item>
    /// </list>
    ///
    /// Matching bare <c>Pending</c> swept the whole mobile approval queue: every mobile order is
    /// created Pending with no DocNum, so orders deliberately never sent to SAP were probed every
    /// two minutes for the entire lookback window. Being the oldest rows they sorted first and
    /// filled every slot, starving the real limbo cases — 86 consecutive sweeps on 2026-08-02
    /// resolved 0 of 22-25, pinned at the cap for 40 minutes, and the only candidates that ever
    /// left the set left because a human re-approved them by hand.
    /// </remarks>
    internal static Expression<Func<SalesOrderEntity, bool>> UnlinkedSapOrderCandidateFilter(DateTime cutoff) =>
        o => (o.SAPDocNum == null || o.SAPDocNum <= 0)
            && o.CreatedAt >= cutoff
            && (o.Status == SalesOrderStatus.Approved
                || ((o.Status == SalesOrderStatus.Pending || o.Status == SalesOrderStatus.Draft)
                    && o.SyncError != null))
            && o.OrderNumber != null
            && o.OrderNumber != "";

    private async Task<Dictionary<string, string>> ResolveSalesOrderLineUomLookupAsync(
        IEnumerable<(string? ItemCode, string? UoMCode)> lines,
        CancellationToken cancellationToken)
    {
        var normalizedLines = lines
            .Select(line => (
                ItemCode: UomQuantityValidation.NormalizeItemCode(line.ItemCode),
                UoMCode: string.IsNullOrWhiteSpace(line.UoMCode) ? null : line.UoMCode.Trim()))
            .ToList();

        var lineUomLookup = await UomQuantityValidation.ResolveUomLookupAsync(
            _context,
            normalizedLines,
            cancellationToken);

        var missingItemCodes = normalizedLines
            .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode)
                && string.IsNullOrWhiteSpace(line.UoMCode)
                && !lineUomLookup.ContainsKey(line.ItemCode!))
            .Select(line => line.ItemCode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missingItemCodes.Count == 0)
        {
            return lineUomLookup;
        }

        // One call for every unresolved code rather than one per code. This runs inside order
        // validation, and a Service Layer round-trip carries roughly a one-in-thirteen chance of
        // stalling several seconds regardless of how little it asks for — so on a multi-line order
        // the number of calls, not the size of any one of them, decided how long a save took.
        Dictionary<string, Item> sapItems;
        try
        {
            sapItems = await _sapClient.GetItemsByCodesAsync(missingItemCodes, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve sales order UoMs from SAP for {Count} items", missingItemCodes.Count);
            return lineUomLookup;
        }

        foreach (var itemCode in missingItemCodes)
        {
            var resolvedUomCode = sapItems.TryGetValue(itemCode, out var item)
                ? NormalizeResolvedUomCode(item.SalesUnit) ?? NormalizeResolvedUomCode(item.InventoryUOM)
                : null;

            if (string.IsNullOrWhiteSpace(resolvedUomCode))
            {
                _logger.LogWarning(
                    "Could not resolve a UoM code for sales order item {ItemCode} from local cache or SAP item master",
                    itemCode);
                continue;
            }

            lineUomLookup[itemCode] = resolvedUomCode;
        }

        return lineUomLookup;
    }

    private static string? NormalizeResolvedUomCode(string? uomCode)
        => string.IsNullOrWhiteSpace(uomCode) ? null : uomCode.Trim();

    private async Task ValidateAndNormalizeSalesOrderRequestAsync(CreateSalesOrderRequest request, CancellationToken cancellationToken)
    {
        var validationErrors = RecursiveDataAnnotationsValidator.Validate(request);

        foreach (var line in request.Lines)
        {
            var normalizedItemCode = UomQuantityValidation.NormalizeItemCode(line.ItemCode);
            if (!string.IsNullOrWhiteSpace(normalizedItemCode))
            {
                line.ItemCode = normalizedItemCode;
            }
        }

        if (string.IsNullOrWhiteSpace(request.Currency))
        {
            validationErrors.Add("Currency is required");
        }
        else
        {
            request.Currency = request.Currency.Trim().ToUpperInvariant();
        }

        var lineUomLookup = await ResolveSalesOrderLineUomLookupAsync(
            request.Lines.Select(line => (ItemCode: (string?)line.ItemCode, line.UoMCode)),
            cancellationToken);

        foreach (var (line, index) in request.Lines.Select((line, index) => (line, index)))
        {
            var resolvedUomCode = UomQuantityValidation.ResolveLineUomCode(line.UoMCode, line.ItemCode, lineUomLookup);
            if (!string.IsNullOrWhiteSpace(resolvedUomCode)
                && !string.Equals(line.UoMCode, resolvedUomCode, StringComparison.Ordinal))
            {
                line.UoMCode = resolvedUomCode;
            }

            if (request.Source == SalesOrderSource.Web && string.IsNullOrWhiteSpace(resolvedUomCode))
            {
                validationErrors.Add(
                    $"Line {index + 1} ({line.ItemCode}) has no sales unit configured locally or in the SAP item master");
            }

            var quantityError = UomQuantityValidation.BuildFractionalQuantityValidationError(index + 1, line.ItemCode, line.Quantity, resolvedUomCode);
            if (!string.IsNullOrWhiteSpace(quantityError))
            {
                validationErrors.Add(quantityError);
            }
        }

        if (validationErrors.Count > 0)
            throw new InvalidOperationException($"Sales order validation failed: {string.Join("; ", validationErrors)}");

        request.CardName = await ResolveCardNameAsync(request.CardCode, request.CardName, cancellationToken);
    }

    /// <summary>
    /// Reports a failed attempt to put an order into SAP, at the level the cause deserves.
    /// </summary>
    /// <remarks>
    /// A credit refusal is a business outcome, not a fault, and it says nothing about SAP: the gate
    /// in <see cref="PostApprovedOrderToSapCoreAsync"/> runs before the create, so no document was
    /// ever attempted. It is logged at Information without a stack, because the handler that catches
    /// <see cref="CreditLimitExceededException"/> already records the rep-facing reason at Warning
    /// and turns it into a result the caller can act on.
    /// <para>
    /// This lived inline at both call sites and both logged Error. The effect in production was that
    /// every error-level line in a nine-hour day was a salesperson hitting a credit limit, under a
    /// message that blamed SAP for it — which is exactly the level an operator alerts on, so the
    /// alert was worthless and the two genuinely broken things that day never reached it.
    /// </para>
    /// </remarks>
    internal void LogFailedSapPost(Exception ex, SalesOrderEntity order, string operation, string rollbackNote)
    {
        if (ex is CreditLimitExceededException)
        {
            _logger.LogInformation(
                "Sales order {OrderId} ({OrderNumber}) was refused on credit before anything was posted to SAP. {RollbackNote}",
                order.Id,
                order.OrderNumber,
                rollbackNote);
            return;
        }

        _logger.LogError(
            ex,
            "Failed to {Operation} sales order {OrderId} ({OrderNumber}) because posting to SAP failed. {RollbackNote}",
            operation,
            order.Id,
            order.OrderNumber,
            rollbackNote);
    }

    /// <summary>
    /// Refuses the order when it would put the customer — or the parent account it draws its limit
    /// from — over the credit limit set in SAP.
    /// </summary>
    private async Task EnsureWithinCreditLimitAsync(
        string? cardCode,
        decimal docTotal,
        CancellationToken cancellationToken)
    {
        var result = await _creditLimitService.CheckSalesOrderAsync(cardCode, docTotal, cancellationToken);

        if (!result.IsWithinLimit)
        {
            throw new CreditLimitExceededException(
                result.Message ?? "This order would exceed the customer's credit limit.");
        }
    }

    /// <summary>Line total net of the line discount, before tax.</summary>
    private static decimal CalculateLineTotal(decimal quantity, decimal unitPrice, decimal discountPercent) =>
        quantity * unitPrice * (1 - discountPercent / 100);

    /// <summary>
    /// The totals an order built from this request will carry. Single source for the document
    /// total so the credit check and the persisted order cannot drift apart.
    /// </summary>
    private (decimal SubTotal, decimal TaxAmount, decimal DiscountAmount, decimal DocTotal) CalculateSalesOrderTotals(
        IEnumerable<CreateSalesOrderLineRequest> lines,
        decimal headerDiscountPercent)
    {
        decimal subTotal = 0;
        decimal taxAmount = 0;

        foreach (var line in lines)
        {
            var lineTotal = CalculateLineTotal(line.Quantity, line.UnitPrice, line.DiscountPercent);
            subTotal += lineTotal;
            taxAmount += lineTotal * ResolveLineTaxPercent(line.TaxPercent) / 100;
        }

        var discountAmount = subTotal * headerDiscountPercent / 100;
        return (subTotal, taxAmount, discountAmount, subTotal - discountAmount + taxAmount);
    }

    private async Task<string?> ResolveCardNameAsync(string? cardCode, string? currentCardName, CancellationToken cancellationToken)
    {
        if (!NeedsResolvedCardName(cardCode, currentCardName))
            return currentCardName;

        var normalizedCardCode = cardCode?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCardCode))
            return currentCardName;

        var businessPartner = await _businessPartnerService.GetBusinessPartnerByCodeAsync(normalizedCardCode, cancellationToken);
        return string.IsNullOrWhiteSpace(businessPartner?.CardName)
            ? currentCardName
            : businessPartner.CardName.Trim();
    }

    private static bool NeedsResolvedCardName(string? cardCode, string? cardName)
    {
        if (string.IsNullOrWhiteSpace(cardCode))
            return false;

        return string.IsNullOrWhiteSpace(cardName) ||
            string.Equals(cardCode.Trim(), cardName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Refuses to post an order carrying a line SAP would accept at 0.00.
    /// </summary>
    /// <remarks>
    /// The SAP payload sends UnitPrice on every line, and a zero is a number like any other —
    /// SAP takes it and does not fall back to its own pricing, so an item the price lookup
    /// failed to resolve goes onto a real customer document free of charge. The pricing pass in
    /// PopulateLivePricesAsync deliberately continues past an item it cannot price, so that a
    /// pricing outage does not strand approvals; this is where that decision stops being safe.
    ///
    /// Posting is the right place for it rather than create: mobile orders arrive with 0.00 on
    /// every line by design and are priced afterwards.
    /// </remarks>
    private void EnsureOrderLinesArePriced(SalesOrderEntity order)
    {
        var unpricedItems = FindUnpricedItemCodes(order);

        if (unpricedItems.Count == 0)
        {
            return;
        }

        _logger.LogError(
            "Refusing to post sales order {OrderNumber} (BP: {CardCode}) to SAP: {UnpricedCount} item(s) have no unit price — {ItemCodes}",
            order.OrderNumber,
            order.CardCode,
            unpricedItems.Count,
            string.Join(", ", unpricedItems));

        throw new InvalidOperationException(
            $"Sales order {order.OrderNumber} cannot be posted to SAP because no price could be found for {string.Join(", ", unpricedItems)}. " +
            "Check the item's price on the customer's price list in SAP, then approve the order again.");
    }

    /// <summary>
    /// The item codes on an order that carry no unit price. Empty means the order is safe to post.
    /// </summary>
    internal static IReadOnlyList<string> FindUnpricedItemCodes(SalesOrderEntity order)
        => (order.Lines ?? [])
            .Where(line => line.UnitPrice <= 0)
            .Select(line => string.IsNullOrWhiteSpace(line.ItemCode) ? "<unknown item>" : line.ItemCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task EnsureOrderHasValidQuantitiesAsync(SalesOrderEntity order, string operation, CancellationToken cancellationToken)
    {
        var validationErrors = new List<string>();

        if (order.Lines == null || order.Lines.Count == 0)
        {
            validationErrors.Add("At least one line item is required");
        }
        else
        {
            foreach (var line in order.Lines)
            {
                var normalizedItemCode = UomQuantityValidation.NormalizeItemCode(line.ItemCode);
                if (!string.IsNullOrWhiteSpace(normalizedItemCode)
                    && !string.Equals(line.ItemCode, normalizedItemCode, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Normalizing sales order item code from {OriginalItemCode} to canonical SAP code {NormalizedItemCode} before {Operation}",
                        line.ItemCode,
                        normalizedItemCode,
                        operation);
                    line.ItemCode = normalizedItemCode;
                }
            }

            var lineUomLookup = await ResolveSalesOrderLineUomLookupAsync(
                order.Lines.Select(line => (ItemCode: (string?)line.ItemCode, line.UoMCode)),
                cancellationToken);

            foreach (var (line, index) in order.Lines.Select((line, index) => (line, index)))
            {
                var resolvedUomCode = UomQuantityValidation.ResolveLineUomCode(line.UoMCode, line.ItemCode, lineUomLookup);
                if (!string.IsNullOrWhiteSpace(resolvedUomCode)
                    && !string.Equals(line.UoMCode, resolvedUomCode, StringComparison.Ordinal))
                {
                    line.UoMCode = resolvedUomCode;
                }

                if (line.Quantity <= 0)
                {
                    validationErrors.Add(
                        $"Line {index + 1} (Item: {line.ItemCode ?? "unknown"}): Quantity must be greater than zero. Current value: {line.Quantity}");
                    continue;
                }

                var quantityError = UomQuantityValidation.BuildFractionalQuantityValidationError(index + 1, line.ItemCode, line.Quantity, resolvedUomCode);
                if (!string.IsNullOrWhiteSpace(quantityError))
                {
                    validationErrors.Add(quantityError);
                }
            }
        }

        if (validationErrors.Count == 0)
            return;

        throw new InvalidOperationException(
            $"Sales order {operation} blocked because the order contains invalid quantities: {string.Join("; ", validationErrors)}");
    }

    private static bool IsDuplicateOrderNumber(DbUpdateException exception)
        => exception.InnerException is PostgresException postgresException
           && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
           && postgresException.ConstraintName?.Contains("OrderNumber", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsDuplicateClientRequestId(DbUpdateException exception)
        => exception.InnerException is PostgresException postgresException
           && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
           && postgresException.ConstraintName?.Contains("ClientRequestId", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The tax rate a line is stored with: whatever the caller asked for, or the configured VAT
    /// rate when it asked for nothing.
    /// </summary>
    /// <remarks>
    /// This used to substitute the configured rate for mobile orders only, which made every web
    /// order's stored tax depend on the create form remembering to send a rate. It did not, so web
    /// orders were persisted with zero-rate lines, a zero tax amount, and a document total equal to
    /// their subtotal — understated against the SAP document, which prices tax from each item's own
    /// tax code regardless of what we send. Making the fallback source-independent is what stops
    /// that recurring; see BackfillWebOrderTaxHandler for the orders written before it did.
    ///
    /// The cost is that a request can no longer express "this line is genuinely zero-rated" by
    /// sending 0 — it reads as "unset". That was already true for mobile, and no caller
    /// distinguishes the two today: SAP owns the real rate, and this value is a local mirror.
    /// </remarks>
    private decimal ResolveLineTaxPercent(decimal requestedTaxPercent)
        => ResolveLineTaxPercent(requestedTaxPercent, _defaultTaxPercent);

    /// <inheritdoc cref="ResolveLineTaxPercent(decimal)"/>
    /// <remarks>
    /// Exposed so the rule can be pinned without standing up the whole service, which is how it
    /// went uncovered long enough to write a database full of zero-tax web orders.
    /// </remarks>
    public static decimal ResolveLineTaxPercent(decimal requestedTaxPercent, decimal defaultTaxPercent)
        => requestedTaxPercent > 0 ? requestedTaxPercent : defaultTaxPercent;

    private static decimal NormalizeTaxPercent(decimal configuredVatRate)
        => configuredVatRate <= 1 ? configuredVatRate * 100m : configuredVatRate;

    private static (string Comments, bool Trimmed) AppendCommentWithinLimit(string? existingComments, string newComment)
    {
        if (newComment.Length >= MaxCommentsLength)
            return (newComment[^MaxCommentsLength..], true);

        if (string.IsNullOrWhiteSpace(existingComments))
            return (newComment, false);

        const string separator = "\n";
        var combined = $"{existingComments}{separator}{newComment}";
        if (combined.Length <= MaxCommentsLength)
            return (combined, false);

        const string ellipsis = "...";
        var availableExistingLength = MaxCommentsLength - newComment.Length - separator.Length;
        if (availableExistingLength <= ellipsis.Length)
            return (newComment, true);

        var retainedExistingLength = availableExistingLength - ellipsis.Length;
        var retainedExisting = existingComments.Length > retainedExistingLength
            ? $"{ellipsis}{existingComments[^retainedExistingLength..]}"
            : existingComments;

        return ($"{retainedExisting}{separator}{newComment}", true);
    }

    #region Mapping Methods

    private static bool IsValidStatusTransition(SalesOrderStatus currentStatus, SalesOrderStatus newStatus)
    {
        if (currentStatus == newStatus)
            return true;

        return currentStatus switch
        {
            SalesOrderStatus.Draft => newStatus is SalesOrderStatus.Pending or SalesOrderStatus.Approved or SalesOrderStatus.Cancelled or SalesOrderStatus.OnHold or SalesOrderStatus.Rejected,
            SalesOrderStatus.Pending => newStatus is SalesOrderStatus.Draft or SalesOrderStatus.Approved or SalesOrderStatus.Cancelled or SalesOrderStatus.OnHold or SalesOrderStatus.Rejected,
            SalesOrderStatus.Approved => newStatus is SalesOrderStatus.PartiallyFulfilled or SalesOrderStatus.Fulfilled or SalesOrderStatus.Cancelled or SalesOrderStatus.OnHold,
            SalesOrderStatus.PartiallyFulfilled => newStatus is SalesOrderStatus.Fulfilled or SalesOrderStatus.Cancelled,
            SalesOrderStatus.Fulfilled => false,
            SalesOrderStatus.Cancelled => newStatus is SalesOrderStatus.Draft,
            SalesOrderStatus.OnHold => newStatus is SalesOrderStatus.Pending or SalesOrderStatus.Approved or SalesOrderStatus.Cancelled or SalesOrderStatus.Rejected,
            SalesOrderStatus.Rejected => newStatus is SalesOrderStatus.Draft or SalesOrderStatus.Pending or SalesOrderStatus.Cancelled,
            _ => false
        };
    }

    private static SalesOrderStatus MapSAPStatusToLocal(string? documentStatus, string? cancelled)
    {
        if (cancelled == "tYES")
            return SalesOrderStatus.Cancelled;

        return documentStatus switch
        {
            "bost_Open" => SalesOrderStatus.Approved,
            "bost_Close" => SalesOrderStatus.Fulfilled,
            _ => SalesOrderStatus.Draft
        };
    }

    /// <summary>
    /// Puts the local audit trail back onto a DTO built from SAP.
    /// </summary>
    /// <remarks>
    /// SAP holds no record of which app user raised or approved a document, so a DTO built by
    /// MapFromSAP has an empty "created by" — which read to users as the originator being
    /// unknown, when it was only unqueried. The local row is the authority on that, and on the
    /// order number the app issued (SAP carries it as U_OrderNumber); the SAP document stays the
    /// authority on everything else, so nothing else is overwritten here.
    /// </remarks>
    private async Task ApplyLocalAuditTrailAsync(SalesOrderDto dto, CancellationToken cancellationToken)
    {
        if (dto.SAPDocEntry is not > 0)
        {
            return;
        }

        try
        {
            var localOrder = await _context.SalesOrders
                .AsNoTracking()
                .Include(o => o.CreatedByUser)
                .Include(o => o.ApprovedByUser)
                .FirstOrDefaultAsync(o => o.SAPDocEntry == dto.SAPDocEntry, cancellationToken);

            if (localOrder == null)
            {
                // A document raised directly in SAP has no local counterpart. Blank is correct here.
                return;
            }

            dto.OrderNumber = localOrder.OrderNumber;
            dto.CreatedByUserId = localOrder.CreatedByUserId;
            dto.CreatedByUserName = localOrder.CreatedByUser?.Username;
            dto.ApprovedByUserId = localOrder.ApprovedByUserId;
            dto.ApprovedByUserName = localOrder.ApprovedByUser?.Username;
            dto.ApprovedDate = localOrder.ApprovedDate;
            dto.CreatedAt = localOrder.CreatedAt;
            dto.Source = localOrder.Source;
        }
        catch (Exception ex)
        {
            // The document itself came back from SAP; losing the originator's name is not a
            // reason to fail the whole read.
            _logger.LogWarning(
                ex,
                "Failed to attach the local audit trail to sales order DocEntry {DocEntry}",
                dto.SAPDocEntry);
        }
    }

    private static SalesOrderDto MapFromSAP(SAPSalesOrder sap)
    {
        DateTime.TryParse(sap.DocDate, out var docDate);
        DateTime.TryParse(sap.DocDueDate, out var dueDate);

        return new SalesOrderDto
        {
            Id = sap.DocEntry,
            SAPDocEntry = sap.DocEntry,
            SAPDocNum = sap.DocNum,
            OrderNumber = $"SAP-{sap.DocNum}",
            OrderDate = docDate,
            DeliveryDate = dueDate,
            CardCode = sap.CardCode ?? string.Empty,
            CardName = sap.CardName,
            CustomerRefNo = sap.NumAtCard,
            Status = MapSAPStatusToLocal(sap.DocumentStatus, sap.Cancelled),
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
            IsSynced = true,
            SyncError = null,
            Lines = sap.DocumentLines?.Select(l => new SalesOrderLineDto
            {
                LineNum = l.LineNum,
                ItemCode = l.ItemCode ?? string.Empty,
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity ?? 0,
                QuantityFulfilled = l.DeliveredQuantity ?? 0,
                UnitPrice = l.UnitPrice ?? 0,
                DiscountPercent = l.DiscountPercent ?? 0,
                LineTotal = l.LineTotal ?? 0,
                WarehouseCode = l.WarehouseCode,
                UoMCode = l.UoMCode,
                CostCentreCode = l.CostingCode
            }).ToList() ?? new List<SalesOrderLineDto>()
        };
    }

    /// <summary>
    /// Populates line prices from live SAP BP pricing,
    /// with BP-specific special prices taking priority over the BP price list.
    /// </summary>
    private async Task PopulateSAPPricesAsync(SalesOrderEntity order, CancellationToken cancellationToken)
    {
        if (order.Lines == null || order.Lines.Count == 0)
            return;

        using var pricingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pricingCts.CancelAfter(ApprovalPricingTimeout);

        try
        {
            var itemCodes = order.Lines
                .Select(line => line.ItemCode)
                .Where(itemCode => !string.IsNullOrWhiteSpace(itemCode))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (itemCodes.Count == 0)
                return;

            _logger.LogInformation(
                "Populating prices for order {OrderId} using live SAP BP pricing for {ItemCount} items (BP: {CardCode})",
                order.Id,
                itemCodes.Count,
                order.CardCode);

            var livePrices = await _sapClient.GetItemPricesForCustomerAsync(order.CardCode, itemCodes, pricingCts.Token);
            var priceMap = livePrices
                .Where(price => !string.IsNullOrWhiteSpace(price.ItemCode) && price.Price > 0)
                .GroupBy(price => price.ItemCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Price, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            var specialPriceLookup = await _sapClient.GetSpecialPricesForBPWithStatusAsync(order.CardCode, itemCodes, pricingCts.Token);

            if (!specialPriceLookup.IsComplete)
            {
                // Falling through to list price would overcharge a customer who has negotiated
                // rates. Special prices are agreed for long validity windows and change rarely, so
                // the synced local copy is a sound stand-in — and it is applied before the live
                // result below, so anything SAP did manage to return still wins.
                var storedSpecialPrices = await _localPriceCatalogService.GetActiveSpecialPricesAsync(
                    order.CardCode,
                    itemCodes,
                    pricingCts.Token);

                foreach (var storedSpecialPrice in storedSpecialPrices)
                {
                    if (storedSpecialPrice.Value > 0)
                    {
                        priceMap[storedSpecialPrice.Key] = storedSpecialPrice.Value;
                    }
                }

                // Approval deliberately continues either way — a special-price outage should not
                // stop reps posting orders — but which prices the document was built from has to
                // be on the record against the order number.
                _logger.LogWarning(
                    "Special prices for order {OrderId} (BP: {CardCode}) could not be fully retrieved from SAP; applied {StoredCount} special price(s) from the local catalog instead. Any item missing from both is priced at list and may be too high.",
                    order.Id,
                    order.CardCode,
                    storedSpecialPrices.Count);
            }

            foreach (var specialPrice in specialPriceLookup.Prices)
            {
                if (!string.IsNullOrWhiteSpace(specialPrice.Key) && specialPrice.Value > 0)
                {
                    priceMap[specialPrice.Key] = specialPrice.Value;
                }
            }

            foreach (var line in order.Lines)
            {
                line.TaxPercent = ResolveLineTaxPercent(line.TaxPercent);
                if (!priceMap.TryGetValue(line.ItemCode, out var unitPrice))
                {
                    _logger.LogWarning(
                        "No live SAP price found for item {ItemCode} in the customer price catalog for BP {CardCode}",
                        line.ItemCode,
                        order.CardCode);
                    continue;
                }

                line.UnitPrice = unitPrice;
                var discountMultiplier = line.DiscountPercent > 0
                    ? (1 - line.DiscountPercent / 100m)
                    : 1m;
                line.LineTotal = Math.Round(line.Quantity * unitPrice * discountMultiplier, 2);
            }

            // Recalculate order totals
            order.SubTotal = order.Lines.Sum(l => l.LineTotal);

            var taxRate = order.Lines.FirstOrDefault()?.TaxPercent ?? 0m;
            order.TaxAmount = taxRate > 0
                ? Math.Round(order.SubTotal * taxRate / 100m, 2)
                : 0m;

            var orderDiscountMultiplier = order.DiscountPercent > 0
                ? (1 - order.DiscountPercent / 100m)
                : 1m;
            order.DocTotal = Math.Round(
                (order.SubTotal + order.TaxAmount) * orderDiscountMultiplier - order.DiscountAmount, 2);

            _logger.LogInformation(
                "Prices populated for order {OrderId}: SubTotal={SubTotal}, Tax={TaxAmount}, Total={DocTotal}",
                order.Id, order.SubTotal, order.TaxAmount, order.DocTotal);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && pricingCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Timed out populating live SAP prices for order {OrderId} (BP: {CardCode}) after {TimeoutSeconds} seconds. Approval will continue with existing prices.",
                order.Id,
                order.CardCode,
                ApprovalPricingTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to populate live SAP prices for order {OrderId} (BP: {CardCode}). Prices may be incomplete.",
                order.Id, order.CardCode);
            // Don't block approval if live pricing fails — order can still be approved
            // but prices might need manual correction
        }
    }

    private static SalesOrderDto MapToDto(SalesOrderEntity entity)
    {
        var status = entity.Status == SalesOrderStatus.Approved && !HasSapDocNum(entity)
            ? SalesOrderStatus.Pending
            : entity.Status;

        return new SalesOrderDto
        {
            Id = entity.Id,
            SAPDocEntry = entity.SAPDocEntry,
            SAPDocNum = entity.SAPDocNum,
            OrderNumber = entity.OrderNumber,
            OrderDate = entity.OrderDate,
            DeliveryDate = entity.DeliveryDate,
            CardCode = entity.CardCode,
            CardName = entity.CardName,
            CustomerRefNo = entity.CustomerRefNo,
            Status = status,
            Comments = entity.Comments,
            SalesPersonCode = entity.SalesPersonCode,
            SalesPersonName = entity.SalesPersonName,
            Currency = entity.Currency,
            ExchangeRate = entity.ExchangeRate,
            SubTotal = entity.SubTotal,
            TaxAmount = entity.TaxAmount,
            DiscountPercent = entity.DiscountPercent,
            DiscountAmount = entity.DiscountAmount,
            DocTotal = entity.DocTotal,
            ShipToAddress = entity.ShipToAddress,
            BillToAddress = entity.BillToAddress,
            WarehouseCode = entity.WarehouseCode,
            CreatedByUserId = entity.CreatedByUserId,
            CreatedByUserName = entity.CreatedByUser?.Username,
            ApprovedByUserId = entity.ApprovedByUserId,
            ApprovedByUserName = entity.ApprovedByUser?.Username,
            ApprovedDate = entity.ApprovedDate,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            InvoiceId = entity.InvoiceId,
            InvoiceSapDocNum = entity.Invoice?.SAPDocNum,
            IsSynced = entity.IsSynced,
            SyncError = entity.SyncError,
            Source = entity.Source,
            ClientRequestId = entity.ClientRequestId,
            MerchandiserNotes = entity.MerchandiserNotes,
            DeviceInfo = entity.DeviceInfo,
            Latitude = entity.Latitude,
            Longitude = entity.Longitude,
            RowVersion = entity.RowVersion != null ? Convert.ToBase64String(entity.RowVersion) : null,
            Lines = entity.Lines.Select(l => new SalesOrderLineDto
            {
                Id = l.Id,
                LineNum = l.LineNum,
                ItemCode = l.ItemCode,
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                QuantityFulfilled = l.QuantityFulfilled,
                UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent,
                TaxPercent = l.TaxPercent,
                LineTotal = l.LineTotal,
                WarehouseCode = l.WarehouseCode,
                UoMCode = l.UoMCode,
                BatchNumber = l.BatchNumber,
                CostCentreCode = l.CostCentreCode
            }).ToList()
        };
    }

    #endregion
}
