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

    public SalesOrderService(
        ApplicationDbContext context,
        ISAPServiceLayerClient sapClient,
        ILogger<SalesOrderService> logger)
    {
        _context = context;
        _sapClient = sapClient;
        _logger = logger;
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
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // Fetch from SAP
            _logger.LogInformation("Fetching sales orders from SAP - Page: {Page}, PageSize: {PageSize}", page, pageSize);

            List<SAPSalesOrder> sapOrders;
            int totalCount;

            if (!string.IsNullOrEmpty(cardCode))
            {
                _logger.LogInformation("Fetching sales orders by customer: {CardCode}", cardCode);
                sapOrders = await _sapClient.GetSalesOrdersByCustomerAsync(cardCode, cancellationToken);
                totalCount = sapOrders.Count;
                sapOrders = sapOrders.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            }
            else if (fromDate.HasValue && toDate.HasValue)
            {
                _logger.LogInformation("Fetching sales orders by date range: {FromDate} - {ToDate}", fromDate, toDate);
                sapOrders = await _sapClient.GetSalesOrdersByDateRangeAsync(fromDate.Value, toDate.Value, cancellationToken);
                totalCount = sapOrders.Count;
                sapOrders = sapOrders.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            }
            else
            {
                _logger.LogInformation("Fetching paged sales orders from SAP");
                sapOrders = await _sapClient.GetPagedSalesOrdersAsync(page, pageSize, cancellationToken);
                _logger.LogInformation("SAP returned {Count} sales orders", sapOrders.Count);

                totalCount = await _sapClient.GetSalesOrdersCountAsync(cardCode, fromDate, toDate, cancellationToken);
                _logger.LogInformation("SAP total count: {TotalCount}", totalCount);
            }

            // Filter by status if provided (status is a local concept, SAP uses DocumentStatus)
            if (status.HasValue)
            {
                sapOrders = sapOrders.Where(o => MapSAPStatusToLocal(o.DocumentStatus, o.Cancelled) == status.Value).ToList();
            }

            var result = new SalesOrderListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                Orders = sapOrders.Select(MapFromSAP).ToList()
            };

            _logger.LogInformation("Returning {OrderCount} sales orders, TotalCount: {TotalCount}, TotalPages: {TotalPages}",
                result.Orders.Count, result.TotalCount, result.TotalPages);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch sales orders from SAP, falling back to local DB");
            return await GetAllFromLocalAsync(page, pageSize, status, cardCode, fromDate, toDate, cancellationToken);
        }
    }

    private async Task<SalesOrderListResponseDto> GetAllFromLocalAsync(int page, int pageSize, SalesOrderStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default)
    {
        var query = _context.SalesOrders
            .Include(o => o.Lines)
            .Include(o => o.CreatedByUser)
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

        var totalCount = await query.CountAsync(cancellationToken);
        var orders = await query
            .OrderByDescending(o => o.OrderDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new SalesOrderListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Orders = orders.Select(MapToDto).ToList()
        };
    }

    public async Task<SalesOrderDto> CreateAsync(CreateSalesOrderRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var orderNumber = await GenerateOrderNumberAsync(cancellationToken);

        var order = new SalesOrderEntity
        {
            OrderNumber = orderNumber,
            OrderDate = DateTime.UtcNow,
            DeliveryDate = request.DeliveryDate,
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
            Status = SalesOrderStatus.Draft
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

        return MapToDto(order);
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

        order.DeliveryDate = request.DeliveryDate;
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

        if (order.Status != SalesOrderStatus.Pending)
            throw new InvalidOperationException("Only pending orders can be approved");

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
