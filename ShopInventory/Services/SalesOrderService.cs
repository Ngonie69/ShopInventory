using Microsoft.EntityFrameworkCore;
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
    private readonly ApplicationDbContext _context;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<SalesOrderService> _logger;
    private readonly INotificationService _notificationService;
    private readonly IBusinessPartnerService _businessPartnerService;

    public SalesOrderService(
        ApplicationDbContext context,
        ISAPServiceLayerClient sapClient,
        ILogger<SalesOrderService> logger,
        INotificationService notificationService,
        IBusinessPartnerService businessPartnerService)
    {
        _context = context;
        _sapClient = sapClient;
        _logger = logger;
        _notificationService = notificationService;
        _businessPartnerService = businessPartnerService;
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
        SalesOrderSource? source = null, CancellationToken cancellationToken = default)
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
            return await GetAllFromLocalAsync(page, pageSize, status, cardCode, fromDate, toDate, source, cancellationToken);
        }

        var localOffset = Math.Max(0, (page - 1) * pageSize);
        var localUnsyncedCount = await GetLocalUnsyncedOrdersCountAsync(status, cardCode, fromDate, toDate, cancellationToken);
        var localPage = await GetLocalUnsyncedOrdersPageAsync(localOffset, pageSize, status, cardCode, fromDate, toDate, cancellationToken);
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
            return await GetAllFromLocalAsync(page, pageSize, status, cardCode, fromDate, toDate, source, cancellationToken);
        }
    }

    private IQueryable<SalesOrderEntity> BuildLocalUnsyncedOrdersQuery(SalesOrderStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken cancellationToken = default)
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

        return query;
    }

    private async Task<int> GetLocalUnsyncedOrdersCountAsync(SalesOrderStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        return await BuildLocalUnsyncedOrdersQuery(status, cardCode, fromDate, toDate, cancellationToken)
            .CountAsync(cancellationToken);
    }

    private async Task<List<SalesOrderDto>> GetLocalUnsyncedOrdersPageAsync(int skip, int take,
        SalesOrderStatus? status = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0)
            return new List<SalesOrderDto>();

        var orders = await BuildLocalUnsyncedOrdersQuery(status, cardCode, fromDate, toDate, cancellationToken)
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
                Source = o.Source,
                MerchandiserNotes = o.MerchandiserNotes,
                DeviceInfo = o.DeviceInfo,
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
        SalesOrderSource? source = null, CancellationToken cancellationToken = default)
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
                Source = o.Source,
                MerchandiserNotes = o.MerchandiserNotes,
                DeviceInfo = o.DeviceInfo,
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
        var orderNumber = await GenerateOrderNumberAsync(cancellationToken);

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
            DeviceInfo = request.DeviceInfo
        };

        decimal subTotal = 0;
        decimal taxAmount = 0;
        int lineNum = 0;

        foreach (var lineRequest in request.Lines)
        {
            var lineTotal = lineRequest.Quantity * lineRequest.UnitPrice * (1 - lineRequest.DiscountPercent / 100);
            var lineTax = lineTotal * lineRequest.TaxPercent / 100;

            var line = new SalesOrderLineEntity
            {
                LineNum = lineNum++,
                ItemCode = lineRequest.ItemCode,
                ItemDescription = lineRequest.ItemDescription,
                Quantity = lineRequest.Quantity,
                UnitPrice = lineRequest.UnitPrice,
                DiscountPercent = lineRequest.DiscountPercent,
                TaxPercent = lineRequest.TaxPercent,
                LineTotal = lineTotal,
                WarehouseCode = lineRequest.WarehouseCode ?? request.WarehouseCode,
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

        _logger.LogInformation("Created sales order {OrderNumber} for customer {CardCode}", orderNumber, request.CardCode);

        // Send push notification for the new sales order
        try
        {
            var username = await _context.Users
                .Where(u => u.Id == userId)
                .Select(u => u.Username)
                .FirstOrDefaultAsync(cancellationToken);

            var source = request.Source == SalesOrderSource.Mobile ? "Mobile" : "Web";
            await _notificationService.CreateSalesOrderNotificationAsync(
                orderNumber, request.CardName ?? request.CardCode, order.DocTotal, source, username, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification for sales order {OrderNumber}", orderNumber);
        }

        // Resolve SAP prices for mobile orders (submitted with UnitPrice=0)
        if (request.Source == SalesOrderSource.Mobile)
        {
            await ResolveMobileOrderPricesAsync(order, cancellationToken);
        }

        // Auto-post web orders to SAP immediately
        if (request.Source == SalesOrderSource.Web || request.Source == null)
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

    private async Task ResolveMobileOrderPricesAsync(SalesOrderEntity order, CancellationToken cancellationToken)
    {
        try
        {
            var bp = await _businessPartnerService.GetBusinessPartnerByCodeAsync(order.CardCode, cancellationToken);
            if (bp?.PriceListNum == null || bp.PriceListNum <= 0)
            {
                _logger.LogWarning("No price list found for customer {CardCode} on mobile order {OrderNumber}", order.CardCode, order.OrderNumber);
                return;
            }

            var prices = await _sapClient.GetPricesByPriceListAsync(bp.PriceListNum.Value, cancellationToken);
            if (prices == null || prices.Count == 0)
            {
                _logger.LogWarning("No prices returned from price list {PriceListNum} for mobile order {OrderNumber}", bp.PriceListNum, order.OrderNumber);
                return;
            }

            var priceLookup = prices.ToDictionary(p => p.ItemCode ?? "", p => p.Price, StringComparer.OrdinalIgnoreCase);

            decimal subTotal = 0;
            decimal taxAmount = 0;
            int updatedLines = 0;

            foreach (var line in order.Lines)
            {
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

            _logger.LogInformation("Resolved prices for mobile order {OrderNumber}: {UpdatedLines}/{TotalLines} lines updated from price list {PriceListNum}",
                order.OrderNumber, updatedLines, order.Lines.Count, bp.PriceListNum);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve prices for mobile order {OrderNumber}. Order saved with submitted prices.", order.OrderNumber);
        }
    }

    public async Task<SalesOrderDto> UpdateAsync(int id, CreateSalesOrderRequest request, CancellationToken cancellationToken = default)
    {
        var order = await _context.SalesOrders
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
            var lineTotal = lineRequest.Quantity * lineRequest.UnitPrice * (1 - lineRequest.DiscountPercent / 100);
            var lineTax = lineTotal * lineRequest.TaxPercent / 100;

            var line = new SalesOrderLineEntity
            {
                LineNum = lineNum++,
                ItemCode = lineRequest.ItemCode,
                ItemDescription = lineRequest.ItemDescription,
                Quantity = lineRequest.Quantity,
                UnitPrice = lineRequest.UnitPrice,
                DiscountPercent = lineRequest.DiscountPercent,
                TaxPercent = lineRequest.TaxPercent,
                LineTotal = lineTotal,
                WarehouseCode = lineRequest.WarehouseCode ?? request.WarehouseCode,
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

        order.Status = status;
        order.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(comments))
            order.Comments = (order.Comments ?? "") + $"\n[{DateTime.UtcNow:yyyy-MM-dd HH:mm}] Status changed to {status}: {comments}";

        await _context.SaveChangesAsync(cancellationToken);
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
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            throw new InvalidOperationException($"Sales order with ID {id} not found");

        if (order.Status != SalesOrderStatus.Draft && order.Status != SalesOrderStatus.Pending)
            throw new InvalidOperationException("Only draft or pending orders can be approved");

        order.Status = SalesOrderStatus.Approved;
        order.ApprovedByUserId = userId;
        order.ApprovedDate = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
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
            .OrderByDescending(o => o.OrderNumber)
            .FirstOrDefaultAsync(cancellationToken);

        var sequence = 1;
        if (lastOrder != null)
        {
            var lastSequence = lastOrder.OrderNumber.Replace(prefix, "");
            if (int.TryParse(lastSequence, out int parsed))
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
            throw new InvalidOperationException("Only approved orders can be posted to SAP");

        if (order.IsSynced)
            throw new InvalidOperationException("This order has already been posted to SAP");

        try
        {
            var sapOrder = await _sapClient.CreateSalesOrderAsync(order, cancellationToken);

            order.SAPDocEntry = sapOrder.DocEntry;
            order.SAPDocNum = sapOrder.DocNum;
            order.IsSynced = true;
            order.SyncError = null;
            order.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Posted sales order {OrderNumber} to SAP as DocEntry={DocEntry}, DocNum={DocNum}",
                order.OrderNumber, sapOrder.DocEntry, sapOrder.DocNum);

            return MapToDto(order);
        }
        catch (Exception ex)
        {
            order.SyncError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogError(ex, "Failed to post sales order {OrderNumber} to SAP", order.OrderNumber);
            throw;
        }
    }

    #region Mapping Methods

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
            Source = entity.Source,
            MerchandiserNotes = entity.MerchandiserNotes,
            DeviceInfo = entity.DeviceInfo,
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
