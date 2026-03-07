using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Service implementation for Purchase Order operations
/// </summary>
public class PurchaseOrderService : IPurchaseOrderService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<PurchaseOrderService> _logger;

    public PurchaseOrderService(
        ApplicationDbContext context,
        ILogger<PurchaseOrderService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<PurchaseOrderDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var order = await _context.PurchaseOrders
            .Include(o => o.Lines)
            .Include(o => o.CreatedByUser)
            .Include(o => o.ApprovedByUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        return order == null ? null : MapToDto(order);
    }

    public async Task<PurchaseOrderDto?> GetByOrderNumberAsync(string orderNumber, CancellationToken cancellationToken = default)
    {
        var order = await _context.PurchaseOrders
            .Include(o => o.Lines)
            .Include(o => o.CreatedByUser)
            .Include(o => o.ApprovedByUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber, cancellationToken);

        return order == null ? null : MapToDto(order);
    }

    public async Task<PurchaseOrderListResponseDto> GetAllAsync(int page, int pageSize, PurchaseOrderStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default)
    {
        var query = _context.PurchaseOrders
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
            .ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PurchaseOrderListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Orders = orders.Select(MapToDto).ToList()
        };
    }

    public async Task<PurchaseOrderDto> CreateAsync(CreatePurchaseOrderRequest request, Guid? userId, CancellationToken cancellationToken = default)
    {
        var orderNumber = await GenerateOrderNumberAsync(cancellationToken);

        var order = new PurchaseOrderEntity
        {
            OrderNumber = orderNumber,
            OrderDate = DateTime.UtcNow,
            DeliveryDate = request.DeliveryDate,
            CardCode = request.CardCode,
            CardName = request.CardName,
            SupplierRefNo = request.SupplierRefNo,
            Comments = request.Comments,
            BuyerCode = request.BuyerCode,
            BuyerName = request.BuyerName,
            Currency = request.Currency ?? "USD",
            DiscountPercent = request.DiscountPercent,
            ShipToAddress = request.ShipToAddress,
            BillToAddress = request.BillToAddress,
            WarehouseCode = request.WarehouseCode,
            CreatedByUserId = userId,
            Status = PurchaseOrderStatus.Draft
        };

        decimal subTotal = 0;
        decimal taxAmount = 0;
        int lineNum = 0;

        foreach (var lineRequest in request.Lines)
        {
            var lineTotal = lineRequest.Quantity * lineRequest.UnitPrice * (1 - lineRequest.DiscountPercent / 100);
            var lineTax = lineTotal * lineRequest.TaxPercent / 100;

            var line = new PurchaseOrderLineEntity
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

        _context.PurchaseOrders.Add(order);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created purchase order {OrderNumber} for supplier {CardCode}", orderNumber, request.CardCode);

        return MapToDto(order);
    }

    public async Task<PurchaseOrderDto> UpdateAsync(int id, CreatePurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        var order = await _context.PurchaseOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            throw new InvalidOperationException($"Purchase order with ID {id} not found");

        if (order.Status != PurchaseOrderStatus.Draft)
            throw new InvalidOperationException("Only draft orders can be edited");

        order.DeliveryDate = request.DeliveryDate;
        order.CardCode = request.CardCode;
        order.CardName = request.CardName;
        order.SupplierRefNo = request.SupplierRefNo;
        order.Comments = request.Comments;
        order.BuyerCode = request.BuyerCode;
        order.BuyerName = request.BuyerName;
        order.Currency = request.Currency ?? order.Currency;
        order.DiscountPercent = request.DiscountPercent;
        order.ShipToAddress = request.ShipToAddress;
        order.BillToAddress = request.BillToAddress;
        order.WarehouseCode = request.WarehouseCode;
        order.UpdatedAt = DateTime.UtcNow;

        // Remove existing lines and add new ones
        _context.PurchaseOrderLines.RemoveRange(order.Lines);
        order.Lines.Clear();

        decimal subTotal = 0;
        decimal taxAmount = 0;
        int lineNum = 0;

        foreach (var lineRequest in request.Lines)
        {
            var lineTotal = lineRequest.Quantity * lineRequest.UnitPrice * (1 - lineRequest.DiscountPercent / 100);
            var lineTax = lineTotal * lineRequest.TaxPercent / 100;

            var line = new PurchaseOrderLineEntity
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

    public async Task<PurchaseOrderDto> UpdateStatusAsync(int id, PurchaseOrderStatus status, Guid? userId, string? comments = null, CancellationToken cancellationToken = default)
    {
        var order = await _context.PurchaseOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            throw new InvalidOperationException($"Purchase order with ID {id} not found");

        order.Status = status;
        order.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(comments))
            order.Comments = (order.Comments ?? "") + $"\n[{DateTime.UtcNow:yyyy-MM-dd HH:mm}] Status changed to {status}: {comments}";

        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(order);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var order = await _context.PurchaseOrders.FindAsync(new object[] { id }, cancellationToken);
        if (order == null)
            return false;

        if (order.Status != PurchaseOrderStatus.Draft && order.Status != PurchaseOrderStatus.Cancelled)
            throw new InvalidOperationException("Only draft or cancelled orders can be deleted");

        _context.PurchaseOrders.Remove(order);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<PurchaseOrderDto> ApproveAsync(int id, Guid? userId, CancellationToken cancellationToken = default)
    {
        var order = await _context.PurchaseOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            throw new InvalidOperationException($"Purchase order with ID {id} not found");

        if (order.Status != PurchaseOrderStatus.Pending)
            throw new InvalidOperationException("Only pending orders can be approved");

        order.Status = PurchaseOrderStatus.Approved;
        order.ApprovedByUserId = userId;
        order.ApprovedDate = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(order);
    }

    public async Task<PurchaseOrderDto> ReceiveItemsAsync(int id, ReceivePurchaseOrderRequest request, Guid? userId, CancellationToken cancellationToken = default)
    {
        var order = await _context.PurchaseOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            throw new InvalidOperationException($"Purchase order with ID {id} not found");

        if (order.Status != PurchaseOrderStatus.Approved && order.Status != PurchaseOrderStatus.PartiallyReceived)
            throw new InvalidOperationException("Only approved or partially received orders can receive items");

        foreach (var receiveLine in request.Lines)
        {
            var orderLine = order.Lines.FirstOrDefault(l => l.LineNum == receiveLine.LineNum && l.ItemCode == receiveLine.ItemCode);
            if (orderLine == null)
                throw new InvalidOperationException($"Line {receiveLine.LineNum} with item {receiveLine.ItemCode} not found on this order");

            var newTotal = orderLine.QuantityReceived + receiveLine.QuantityReceived;
            if (newTotal > orderLine.Quantity)
                throw new InvalidOperationException($"Cannot receive more than ordered for {receiveLine.ItemCode}. Ordered: {orderLine.Quantity}, Already received: {orderLine.QuantityReceived}, Attempting: {receiveLine.QuantityReceived}");

            orderLine.QuantityReceived = newTotal;

            if (!string.IsNullOrEmpty(receiveLine.BatchNumber))
                orderLine.BatchNumber = receiveLine.BatchNumber;
        }

        // Determine new status
        var allReceived = order.Lines.All(l => l.QuantityReceived >= l.Quantity);
        var anyReceived = order.Lines.Any(l => l.QuantityReceived > 0);

        if (allReceived)
            order.Status = PurchaseOrderStatus.Received;
        else if (anyReceived)
            order.Status = PurchaseOrderStatus.PartiallyReceived;

        order.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(request.Comments))
            order.Comments = (order.Comments ?? "") + $"\n[{DateTime.UtcNow:yyyy-MM-dd HH:mm}] Goods received: {request.Comments}";

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Received items for purchase order {OrderNumber}. New status: {Status}",
            order.OrderNumber, order.Status);

        return MapToDto(order);
    }

    public async Task<string> GenerateOrderNumberAsync(CancellationToken cancellationToken = default)
    {
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        var prefix = $"PO-{today}-";

        var lastOrder = await _context.PurchaseOrders
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

    private static PurchaseOrderDto MapToDto(PurchaseOrderEntity entity)
    {
        return new PurchaseOrderDto
        {
            Id = entity.Id,
            SAPDocEntry = entity.SAPDocEntry,
            SAPDocNum = entity.SAPDocNum,
            OrderNumber = entity.OrderNumber,
            OrderDate = entity.OrderDate,
            DeliveryDate = entity.DeliveryDate,
            CardCode = entity.CardCode,
            CardName = entity.CardName,
            SupplierRefNo = entity.SupplierRefNo,
            Status = entity.Status,
            Comments = entity.Comments,
            BuyerCode = entity.BuyerCode,
            BuyerName = entity.BuyerName,
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
            IsSynced = entity.IsSynced,
            Lines = entity.Lines.Select(l => new PurchaseOrderLineDto
            {
                Id = l.Id,
                LineNum = l.LineNum,
                ItemCode = l.ItemCode,
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                QuantityReceived = l.QuantityReceived,
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
