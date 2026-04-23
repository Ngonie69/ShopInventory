using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ShopInventory.Common.Validation;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
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

    private readonly ApplicationDbContext _context;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<SalesOrderService> _logger;
    private readonly INotificationService _notificationService;
    private readonly IBusinessPartnerService _businessPartnerService;
    private readonly decimal _defaultMobileTaxPercent;

    public SalesOrderService(
        ApplicationDbContext context,
        ISAPServiceLayerClient sapClient,
        ILogger<SalesOrderService> logger,
        INotificationService notificationService,
        IBusinessPartnerService businessPartnerService,
        IOptions<RevmaxSettings> revmaxSettings)
    {
        _context = context;
        _sapClient = sapClient;
        _logger = logger;
        _notificationService = notificationService;
        _businessPartnerService = businessPartnerService;
        _defaultMobileTaxPercent = NormalizeTaxPercent(revmaxSettings.Value.VatRate);
    }

    public async Task<SalesOrderDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        // Try to get from SAP first (by DocEntry)
        try
        {
            var sapOrder = await _sapClient.GetSalesOrderByDocEntryAsync(id, cancellationToken);
            if (sapOrder != null)
                return MapFromSAP(sapOrder);
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
            query = query.Where(o => o.Status == status.Value);

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
                    BatchNumber = l.BatchNumber
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
            query = query.Where(o => o.Status == status.Value);

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
        var orders = await query
            .OrderByDescending(o => o.OrderDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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
                    BatchNumber = l.BatchNumber
                }).ToList()
            })
            .ToListAsync(cancellationToken);

        return new SalesOrderListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Orders = orders
        };
    }

    public async Task<SalesOrderDto> CreateAsync(CreateSalesOrderRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        await ValidateAndNormalizeSalesOrderRequestAsync(request, cancellationToken);

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
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var order = new SalesOrderEntity
                {
                    OrderNumber = orderNumber,
                    OrderDate = DateTime.UtcNow,
                    DeliveryDate = request.DeliveryDate.HasValue
                        ? DateTime.SpecifyKind(request.DeliveryDate.Value, DateTimeKind.Utc)
                        : null,
                    CardCode = request.CardCode,
                    CardName = request.CardName,
                    CustomerRefNo = request.CustomerRefNo,
                    Comments = request.Comments,
                    SalesPersonCode = request.SalesPersonCode,
                    SalesPersonName = request.SalesPersonName,
                    Currency = request.Currency ?? "USD",
                    DiscountPercent = request.DiscountPercent,
                    ShipToAddress = request.ShipToAddress,
                    BillToAddress = request.BillToAddress,
                    WarehouseCode = request.WarehouseCode,
                    CreatedByUserId = userId,
                    Status = SalesOrderStatus.Draft,
                    Source = request.Source,
                    MerchandiserNotes = request.MerchandiserNotes,
                    DeviceInfo = request.DeviceInfo,
                    Latitude = request.Latitude,
                    Longitude = request.Longitude
                };

                decimal subTotal = 0;
                decimal taxAmount = 0;
                int lineNum = 0;

                foreach (var lineRequest in request.Lines)
                {
                    var taxPercent = ResolveLineTaxPercent(lineRequest.TaxPercent, request.Source);
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
                        BatchNumber = lineRequest.BatchNumber
                    };

                    order.Lines.Add(line);
                    subTotal += lineTotal;
                    taxAmount += lineTax;
                }

                order.SubTotal = subTotal;
                order.TaxAmount = taxAmount;
                order.DiscountAmount = subTotal * request.DiscountPercent / 100;
                order.DocTotal = subTotal - order.DiscountAmount + taxAmount;

                _context.SalesOrders.Add(order);
                await _context.SaveChangesAsync(cancellationToken);

                var persistedLineCount = await _context.SalesOrderLines
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
                        order.Status = SalesOrderStatus.Approved;
                        await _context.SaveChangesAsync(cancellationToken);

                        var sapOrder = await _sapClient.CreateSalesOrderAsync(order, cancellationToken);

                        order.SAPDocEntry = sapOrder.DocEntry;
                        order.SAPDocNum = sapOrder.DocNum;
                        order.IsSynced = true;
                        order.SyncError = null;
                        order.UpdatedAt = DateTime.UtcNow;

                        await _context.SaveChangesAsync(cancellationToken);

                        _logger.LogInformation("Auto-posted sales order {OrderNumber} to SAP as DocEntry={DocEntry}, DocNum={DocNum}",
                            order.OrderNumber, sapOrder.DocEntry, sapOrder.DocNum);
                    }
                    catch (Exception ex)
                    {
                        order.SyncError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                        order.UpdatedAt = DateTime.UtcNow;
                        await _context.SaveChangesAsync(cancellationToken);

                        _logger.LogWarning(ex, "Auto-post to SAP failed for order {OrderNumber}. Order saved locally as Approved.", order.OrderNumber);
                    }
                }

                return MapToDto(order);
            }
            catch (DbUpdateException ex) when (IsDuplicateOrderNumber(ex) && attempt < MaxCreateOrderAttempts)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _context.ChangeTracker.Clear();

                _logger.LogWarning(ex,
                    "Order number collision while creating sales order for customer {CardCode}. Retrying attempt {Attempt} of {MaxAttempts}.",
                    request.CardCode,
                    attempt,
                    MaxCreateOrderAttempts);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
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
                using var priceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                priceCts.CancelAfter(TimeSpan.FromSeconds(180));
                await ResolveMobileOrderPricesAsync(order, priceCts.Token);

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

        // Single targeted SQL query: joins OCRD (customer price list) + ITM1 (prices)
        // for only the items on this order — eliminates separate BP lookup + full list fetch
        var prices = await _sapClient.GetItemPricesForCustomerAsync(order.CardCode, itemCodes, cancellationToken);
        if (prices == null || prices.Count == 0)
        {
            _logger.LogWarning("No prices returned for customer {CardCode} items on mobile order {OrderNumber}", order.CardCode, order.OrderNumber);
            return;
        }

        var priceLookup = prices.ToDictionary(p => p.ItemCode ?? "", p => p.Price, StringComparer.OrdinalIgnoreCase);

        decimal subTotal = 0;
        decimal taxAmount = 0;
        int updatedLines = 0;

        foreach (var line in order.Lines)
        {
            line.TaxPercent = ResolveLineTaxPercent(line.TaxPercent, order.Source);

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
                order.OrderNumber,
                order.CardName ?? order.CardCode,
                order.DocTotal,
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

        _logger.LogError(
            ex,
            "Failed durable post-save processing for mobile sales order {SalesOrderId}. Queue entry {QueueId} moved to {Status}.",
            queueEntry.SalesOrderId,
            queueEntry.Id,
            queueEntry.Status);
    }

    public async Task<SalesOrderDto> UpdateAsync(int id, CreateSalesOrderRequest request, CancellationToken cancellationToken = default)
    {
        await ValidateAndNormalizeSalesOrderRequestAsync(request, cancellationToken);

        var order = await _context.SalesOrders
            .AsTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            throw new InvalidOperationException($"Sales order with ID {id} not found");

        if (order.Status != SalesOrderStatus.Draft)
            throw new InvalidOperationException("Only draft orders can be edited");

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
        order.Currency = request.Currency ?? order.Currency;
        order.DiscountPercent = request.DiscountPercent;
        order.ShipToAddress = request.ShipToAddress;
        order.BillToAddress = request.BillToAddress;
        order.WarehouseCode = request.WarehouseCode;
        order.MerchandiserNotes = request.MerchandiserNotes ?? order.MerchandiserNotes;
        order.UpdatedAt = DateTime.UtcNow;

        // Remove existing lines and add new ones
        _context.SalesOrderLines.RemoveRange(order.Lines);
        order.Lines.Clear();

        decimal subTotal = 0;
        decimal taxAmount = 0;
        int lineNum = 0;

        foreach (var lineRequest in request.Lines)
        {
            var taxPercent = ResolveLineTaxPercent(lineRequest.TaxPercent, order.Source);
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
                BatchNumber = lineRequest.BatchNumber
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
        var order = await _context.SalesOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            throw new InvalidOperationException($"Sales order with ID {id} not found");

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
        var order = await _context.SalesOrders.FindAsync(new object[] { id }, cancellationToken);
        if (order == null)
            return false;

        if (order.Status != SalesOrderStatus.Draft && order.Status != SalesOrderStatus.Cancelled)
            throw new InvalidOperationException("Only draft or cancelled orders can be deleted");

        _context.SalesOrders.Remove(order);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<SalesOrderDto> ApproveAsync(int id, Guid userId, CancellationToken cancellationToken = default)
    {
        var order = await _context.SalesOrders
            .AsTracking()
            .Include(o => o.Lines)
            .Include(o => o.CreatedByUser)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            throw new InvalidOperationException($"Sales order with ID {id} not found");

        if (order.Status != SalesOrderStatus.Draft && order.Status != SalesOrderStatus.Pending)
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

        order.Status = SalesOrderStatus.Approved;
        order.ApprovedByUserId = userId;
        order.ApprovedDate = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            await PostApprovedOrderToSapAsync(order, cancellationToken);
        }
        catch (Exception ex)
        {
            order.Status = originalStatus;
            order.Comments = originalComments;
            order.ApprovedByUserId = originalApprovedByUserId;
            order.ApprovedDate = originalApprovedDate;
            order.SAPDocEntry = originalSapDocEntry;
            order.SAPDocNum = originalSapDocNum;
            order.IsSynced = originalIsSynced;
            order.SyncError = TruncateSyncError(ex.Message);
            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogError(
                ex,
                "Failed to approve sales order {OrderId} ({OrderNumber}) because posting to SAP failed. Approval state was rolled back.",
                order.Id,
                order.OrderNumber);

            throw;
        }

        _logger.LogInformation(
            "Approved sales order {OrderId} ({OrderNumber}) by user {UserId} and posted it to SAP as DocEntry={DocEntry}, DocNum={DocNum}",
            order.Id,
            order.OrderNumber,
            userId,
            order.SAPDocEntry,
            order.SAPDocNum);

        // Reload with approver navigation for DTO mapping
        await _context.Entry(order).Reference(o => o.ApprovedByUser).LoadAsync(cancellationToken);

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
        order.InvoiceId = invoice.Id;
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

        if (order.IsSynced)
            throw new InvalidOperationException("This order has already been posted to SAP");

        try
        {
            await PostApprovedOrderToSapAsync(order, cancellationToken);

            return MapToDto(order);
        }
        catch (Exception ex)
        {
            order.SyncError = TruncateSyncError(ex.Message);
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogError(ex, "Failed to post sales order {OrderNumber} to SAP", order.OrderNumber);
            throw;
        }
    }

    private async Task PostApprovedOrderToSapAsync(SalesOrderEntity order, CancellationToken cancellationToken)
    {
        await EnsureOrderHasValidQuantitiesAsync(order, "posting to SAP", cancellationToken);

        var sapOrder = await _sapClient.CreateSalesOrderAsync(order, cancellationToken);

        order.SAPDocEntry = sapOrder.DocEntry;
        order.SAPDocNum = sapOrder.DocNum;
        order.IsSynced = true;
        order.SyncError = null;
        order.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Posted sales order {OrderNumber} to SAP as DocEntry={DocEntry}, DocNum={DocNum}",
            order.OrderNumber,
            sapOrder.DocEntry,
            sapOrder.DocNum);
    }

    private static string TruncateSyncError(string message)
        => message.Length > 500 ? message[..500] : message;

    private static string TruncateMobileQueueError(string message)
        => message.Length > 2000 ? message[..2000] : message;

    private async Task ValidateAndNormalizeSalesOrderRequestAsync(CreateSalesOrderRequest request, CancellationToken cancellationToken)
    {
        var validationErrors = RecursiveDataAnnotationsValidator.Validate(request);
        var lineUomLookup = await UomQuantityValidation.ResolveUomLookupAsync(
            _context,
            request.Lines.Select(line => (ItemCode: (string?)line.ItemCode, line.UoMCode)),
            cancellationToken);

        foreach (var (line, index) in request.Lines.Select((line, index) => (line, index)))
        {
            var resolvedUomCode = UomQuantityValidation.ResolveLineUomCode(line.UoMCode, line.ItemCode, lineUomLookup);
            if (string.IsNullOrWhiteSpace(line.UoMCode) && !string.IsNullOrWhiteSpace(resolvedUomCode))
            {
                line.UoMCode = resolvedUomCode;
            }

            var quantityError = UomQuantityValidation.BuildFractionalQuantityValidationError(index + 1, line.ItemCode, line.Quantity, resolvedUomCode);
            if (!string.IsNullOrWhiteSpace(quantityError))
            {
                validationErrors.Add(quantityError);
            }
        }

        if (validationErrors.Count == 0)
            return;

        throw new InvalidOperationException($"Sales order validation failed: {string.Join("; ", validationErrors)}");
    }

    private async Task EnsureOrderHasValidQuantitiesAsync(SalesOrderEntity order, string operation, CancellationToken cancellationToken)
    {
        var validationErrors = new List<string>();

        if (order.Lines == null || order.Lines.Count == 0)
        {
            validationErrors.Add("At least one line item is required");
        }
        else
        {
            var lineUomLookup = await UomQuantityValidation.ResolveUomLookupAsync(
                _context,
                order.Lines.Select(line => (ItemCode: (string?)line.ItemCode, line.UoMCode)),
                cancellationToken);

            foreach (var (line, index) in order.Lines.Select((line, index) => (line, index)))
            {
                var resolvedUomCode = UomQuantityValidation.ResolveLineUomCode(line.UoMCode, line.ItemCode, lineUomLookup);
                if (string.IsNullOrWhiteSpace(line.UoMCode) && !string.IsNullOrWhiteSpace(resolvedUomCode))
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

    private decimal ResolveLineTaxPercent(decimal requestedTaxPercent, SalesOrderSource source)
    {
        if (requestedTaxPercent > 0)
            return requestedTaxPercent;

        return source == SalesOrderSource.Mobile ? _defaultMobileTaxPercent : requestedTaxPercent;
    }

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
            SalesOrderStatus.Draft => newStatus is SalesOrderStatus.Pending or SalesOrderStatus.Approved or SalesOrderStatus.Cancelled or SalesOrderStatus.OnHold,
            SalesOrderStatus.Pending => newStatus is SalesOrderStatus.Draft or SalesOrderStatus.Approved or SalesOrderStatus.Cancelled or SalesOrderStatus.OnHold,
            SalesOrderStatus.Approved => newStatus is SalesOrderStatus.PartiallyFulfilled or SalesOrderStatus.Fulfilled or SalesOrderStatus.Cancelled or SalesOrderStatus.OnHold,
            SalesOrderStatus.PartiallyFulfilled => newStatus is SalesOrderStatus.Fulfilled or SalesOrderStatus.Cancelled,
            SalesOrderStatus.Fulfilled => false,
            SalesOrderStatus.Cancelled => false,
            SalesOrderStatus.OnHold => newStatus is SalesOrderStatus.Pending or SalesOrderStatus.Approved or SalesOrderStatus.Cancelled,
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
                UoMCode = l.UoMCode
            }).ToList() ?? new List<SalesOrderLineDto>()
        };
    }

    /// <summary>
    /// Populates line prices from SAP using the BP's assigned price list,
    /// with BP-specific special prices (OSPP) taking priority.
    /// </summary>
    private async Task PopulateSAPPricesAsync(SalesOrderEntity order, CancellationToken cancellationToken)
    {
        if (order.Lines == null || order.Lines.Count == 0)
            return;

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
                "Populating prices for order {OrderId} using targeted SAP lookup for {ItemCount} items (BP: {CardCode})",
                order.Id,
                itemCodes.Count,
                order.CardCode);

            var priceListPrices = await _sapClient.GetItemPricesForCustomerAsync(order.CardCode, itemCodes, cancellationToken);
            var priceMap = priceListPrices
                .Where(p => p.ItemCode != null)
                .ToDictionary(p => p.ItemCode!, p => p.Price, StringComparer.OrdinalIgnoreCase);

            var specialPrices = await _sapClient.GetSpecialPricesForBPAsync(order.CardCode, itemCodes, cancellationToken);

            if (specialPrices.Count > 0)
            {
                _logger.LogInformation(
                    "Found {Count} special prices for BP {CardCode}, these will override price list values",
                    specialPrices.Count, order.CardCode);
            }

            // Apply prices to each line: special price > price list price
            foreach (var line in order.Lines)
            {
                line.TaxPercent = ResolveLineTaxPercent(line.TaxPercent, order.Source);
                decimal unitPrice = 0;

                // Special price takes priority
                if (specialPrices.TryGetValue(line.ItemCode, out var specialPrice))
                {
                    unitPrice = specialPrice;
                }
                else if (priceMap.TryGetValue(line.ItemCode, out var listPrice))
                {
                    unitPrice = listPrice;
                }
                else
                {
                    _logger.LogWarning(
                        "No price found for item {ItemCode} in targeted customer pricing or special prices for BP {CardCode}",
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
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to populate SAP prices for order {OrderId} (BP: {CardCode}). Prices may be incomplete.",
                order.Id, order.CardCode);
            // Don't block approval if SAP pricing fails — order can still be approved
            // but prices might need manual correction
        }
    }

    private static SalesOrderDto MapToDto(SalesOrderEntity entity)
    {
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
            Status = entity.Status,
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
            IsSynced = entity.IsSynced,
            SyncError = entity.SyncError,
            Source = entity.Source,
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
                BatchNumber = l.BatchNumber
            }).ToList()
        };
    }

    #endregion
}
