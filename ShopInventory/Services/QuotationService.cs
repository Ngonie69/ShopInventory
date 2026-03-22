using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

public class QuotationService : IQuotationService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<QuotationService> _logger;

    public QuotationService(ApplicationDbContext context, ILogger<QuotationService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<QuotationDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var quotation = await _context.Quotations
            .Include(q => q.Lines)
            .Include(q => q.CreatedByUser)
            .Include(q => q.ApprovedByUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

        return quotation == null ? null : MapToDto(quotation);
    }

    public async Task<QuotationDto?> GetByQuotationNumberAsync(string quotationNumber, CancellationToken cancellationToken = default)
    {
        var quotation = await _context.Quotations
            .Include(q => q.Lines)
            .Include(q => q.CreatedByUser)
            .Include(q => q.ApprovedByUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.QuotationNumber == quotationNumber, cancellationToken);

        return quotation == null ? null : MapToDto(quotation);
    }

    public async Task<QuotationListResponseDto> GetAllAsync(int page, int pageSize, QuotationStatus? status = null,
        string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null, CancellationToken cancellationToken = default)
    {
        var query = _context.Quotations
            .Include(q => q.Lines)
            .Include(q => q.CreatedByUser)
            .AsNoTracking()
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(q => q.Status == status.Value);

        if (!string.IsNullOrEmpty(cardCode))
            query = query.Where(q => q.CardCode == cardCode);

        if (fromDate.HasValue)
            query = query.Where(q => q.QuotationDate >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(q => q.QuotationDate <= toDate.Value);

        var totalCount = await query.CountAsync(cancellationToken);
        var quotations = await query
            .OrderByDescending(q => q.QuotationDate)
            .ThenByDescending(q => q.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new QuotationListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            Quotations = quotations.Select(MapToDto).ToList()
        };
    }

    public async Task<QuotationDto> CreateAsync(CreateQuotationRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var quotationNumber = await GenerateQuotationNumberAsync(cancellationToken);

        var quotation = new QuotationEntity
        {
            QuotationNumber = quotationNumber,
            QuotationDate = DateTime.UtcNow,
            ValidUntil = request.ValidUntil.HasValue
                ? DateTime.SpecifyKind(request.ValidUntil.Value, DateTimeKind.Utc)
                : DateTime.UtcNow.AddDays(30),
            CardCode = request.CardCode,
            CardName = request.CardName,
            CustomerRefNo = request.CustomerRefNo,
            ContactPerson = request.ContactPerson,
            Comments = request.Comments,
            TermsAndConditions = request.TermsAndConditions,
            SalesPersonCode = request.SalesPersonCode,
            SalesPersonName = request.SalesPersonName,
            Currency = request.Currency ?? "USD",
            DiscountPercent = request.DiscountPercent,
            ShipToAddress = request.ShipToAddress,
            BillToAddress = request.BillToAddress,
            WarehouseCode = request.WarehouseCode,
            CreatedByUserId = userId,
            Status = QuotationStatus.Draft
        };

        decimal subTotal = 0;
        decimal taxAmount = 0;
        int lineNum = 0;

        foreach (var lineRequest in request.Lines)
        {
            var lineTotal = lineRequest.Quantity * lineRequest.UnitPrice * (1 - lineRequest.DiscountPercent / 100);
            var lineTax = lineTotal * lineRequest.TaxPercent / 100;

            var line = new QuotationLineEntity
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
                UoMCode = lineRequest.UoMCode
            };

            quotation.Lines.Add(line);
            subTotal += lineTotal;
            taxAmount += lineTax;
        }

        quotation.SubTotal = subTotal;
        quotation.TaxAmount = taxAmount;
        quotation.DiscountAmount = subTotal * request.DiscountPercent / 100;
        quotation.DocTotal = subTotal - quotation.DiscountAmount + taxAmount;

        _context.Quotations.Add(quotation);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created quotation {QuotationNumber} for customer {CardCode}", quotationNumber, request.CardCode);

        return MapToDto(quotation);
    }

    public async Task<QuotationDto> UpdateAsync(int id, CreateQuotationRequest request, CancellationToken cancellationToken = default)
    {
        var quotation = await _context.Quotations
            .Include(q => q.Lines)
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

        if (quotation == null)
            throw new InvalidOperationException($"Quotation with ID {id} not found");

        if (quotation.Status != QuotationStatus.Draft)
            throw new InvalidOperationException("Only draft quotations can be edited");

        quotation.ValidUntil = request.ValidUntil.HasValue
            ? DateTime.SpecifyKind(request.ValidUntil.Value, DateTimeKind.Utc)
            : quotation.ValidUntil;
        quotation.CardCode = request.CardCode;
        quotation.CardName = request.CardName;
        quotation.CustomerRefNo = request.CustomerRefNo;
        quotation.ContactPerson = request.ContactPerson;
        quotation.Comments = request.Comments;
        quotation.TermsAndConditions = request.TermsAndConditions;
        quotation.SalesPersonCode = request.SalesPersonCode;
        quotation.SalesPersonName = request.SalesPersonName;
        quotation.Currency = request.Currency ?? quotation.Currency;
        quotation.DiscountPercent = request.DiscountPercent;
        quotation.ShipToAddress = request.ShipToAddress;
        quotation.BillToAddress = request.BillToAddress;
        quotation.WarehouseCode = request.WarehouseCode;
        quotation.UpdatedAt = DateTime.UtcNow;

        // Remove existing lines and add new ones
        _context.QuotationLines.RemoveRange(quotation.Lines);
        quotation.Lines.Clear();

        decimal subTotal = 0;
        decimal taxAmount = 0;
        int lineNum = 0;

        foreach (var lineRequest in request.Lines)
        {
            var lineTotal = lineRequest.Quantity * lineRequest.UnitPrice * (1 - lineRequest.DiscountPercent / 100);
            var lineTax = lineTotal * lineRequest.TaxPercent / 100;

            var line = new QuotationLineEntity
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
                UoMCode = lineRequest.UoMCode
            };

            quotation.Lines.Add(line);
            subTotal += lineTotal;
            taxAmount += lineTax;
        }

        quotation.SubTotal = subTotal;
        quotation.TaxAmount = taxAmount;
        quotation.DiscountAmount = subTotal * request.DiscountPercent / 100;
        quotation.DocTotal = subTotal - quotation.DiscountAmount + taxAmount;

        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(quotation);
    }

    public async Task<QuotationDto> UpdateStatusAsync(int id, QuotationStatus status, Guid userId, string? comments = null, CancellationToken cancellationToken = default)
    {
        var quotation = await _context.Quotations
            .Include(q => q.Lines)
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

        if (quotation == null)
            throw new InvalidOperationException($"Quotation with ID {id} not found");

        quotation.Status = status;
        quotation.UpdatedAt = DateTime.UtcNow;

        if (!string.IsNullOrEmpty(comments))
            quotation.Comments = (quotation.Comments ?? "") + $"\n[{DateTime.UtcNow:yyyy-MM-dd HH:mm}] Status changed to {status}: {comments}";

        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(quotation);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var quotation = await _context.Quotations.FindAsync(new object[] { id }, cancellationToken);
        if (quotation == null)
            return false;

        if (quotation.Status != QuotationStatus.Draft && quotation.Status != QuotationStatus.Cancelled)
            throw new InvalidOperationException("Only draft or cancelled quotations can be deleted");

        _context.Quotations.Remove(quotation);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<QuotationDto> ApproveAsync(int id, Guid userId, CancellationToken cancellationToken = default)
    {
        var quotation = await _context.Quotations
            .Include(q => q.Lines)
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

        if (quotation == null)
            throw new InvalidOperationException($"Quotation with ID {id} not found");

        if (quotation.Status != QuotationStatus.Pending && quotation.Status != QuotationStatus.Draft)
            throw new InvalidOperationException("Only draft or pending quotations can be approved");

        quotation.Status = QuotationStatus.Approved;
        quotation.ApprovedByUserId = userId;
        quotation.ApprovedDate = DateTime.UtcNow;
        quotation.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(quotation);
    }

    public async Task<SalesOrderDto?> ConvertToSalesOrderAsync(int id, Guid userId, CancellationToken cancellationToken = default)
    {
        var quotation = await _context.Quotations
            .Include(q => q.Lines)
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

        if (quotation == null)
            throw new InvalidOperationException($"Quotation with ID {id} not found");

        if (quotation.Status != QuotationStatus.Approved && quotation.Status != QuotationStatus.Accepted)
            throw new InvalidOperationException("Only approved or accepted quotations can be converted to sales orders");

        if (quotation.ValidUntil.HasValue && quotation.ValidUntil.Value < DateTime.UtcNow)
            throw new InvalidOperationException("This quotation has expired and cannot be converted");

        // Generate sales order number
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

        var orderNumber = $"{prefix}{sequence:D4}";

        var salesOrder = new SalesOrderEntity
        {
            OrderNumber = orderNumber,
            OrderDate = DateTime.UtcNow,
            DeliveryDate = quotation.ValidUntil,
            CardCode = quotation.CardCode,
            CardName = quotation.CardName,
            CustomerRefNo = quotation.CustomerRefNo,
            Comments = $"Created from Quotation {quotation.QuotationNumber}",
            SalesPersonCode = quotation.SalesPersonCode,
            SalesPersonName = quotation.SalesPersonName,
            Currency = quotation.Currency,
            ExchangeRate = quotation.ExchangeRate,
            DiscountPercent = quotation.DiscountPercent,
            ShipToAddress = quotation.ShipToAddress,
            BillToAddress = quotation.BillToAddress,
            WarehouseCode = quotation.WarehouseCode,
            CreatedByUserId = userId,
            Status = SalesOrderStatus.Draft,
            SubTotal = quotation.SubTotal,
            TaxAmount = quotation.TaxAmount,
            DiscountAmount = quotation.DiscountAmount,
            DocTotal = quotation.DocTotal
        };

        foreach (var line in quotation.Lines)
        {
            salesOrder.Lines.Add(new SalesOrderLineEntity
            {
                LineNum = line.LineNum,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                DiscountPercent = line.DiscountPercent,
                TaxPercent = line.TaxPercent,
                LineTotal = line.LineTotal,
                WarehouseCode = line.WarehouseCode,
                UoMCode = line.UoMCode
            });
        }

        _context.SalesOrders.Add(salesOrder);

        // Update quotation status
        quotation.Status = QuotationStatus.Converted;
        quotation.SalesOrderId = salesOrder.Id;
        quotation.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        // Update the SalesOrderId after save (EF generates the ID)
        quotation.SalesOrderId = salesOrder.Id;
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Converted quotation {QuotationNumber} to sales order {OrderNumber}", quotation.QuotationNumber, orderNumber);

        return new SalesOrderDto
        {
            Id = salesOrder.Id,
            OrderNumber = salesOrder.OrderNumber,
            OrderDate = salesOrder.OrderDate,
            DeliveryDate = salesOrder.DeliveryDate,
            CardCode = salesOrder.CardCode,
            CardName = salesOrder.CardName,
            Status = salesOrder.Status,
            Currency = salesOrder.Currency,
            SubTotal = salesOrder.SubTotal,
            TaxAmount = salesOrder.TaxAmount,
            DiscountPercent = salesOrder.DiscountPercent,
            DiscountAmount = salesOrder.DiscountAmount,
            DocTotal = salesOrder.DocTotal
        };
    }

    public async Task<string> GenerateQuotationNumberAsync(CancellationToken cancellationToken = default)
    {
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        var prefix = $"QT-{today}-";

        var lastQuotation = await _context.Quotations
            .Where(q => q.QuotationNumber.StartsWith(prefix))
            .OrderByDescending(q => q.QuotationNumber)
            .FirstOrDefaultAsync(cancellationToken);

        var sequence = 1;
        if (lastQuotation != null)
        {
            var lastSequence = lastQuotation.QuotationNumber.Replace(prefix, "");
            if (int.TryParse(lastSequence, out int parsed))
                sequence = parsed + 1;
        }

        return $"{prefix}{sequence:D4}";
    }

    #region Mapping Methods

    private static QuotationDto MapToDto(QuotationEntity entity)
    {
        return new QuotationDto
        {
            Id = entity.Id,
            SAPDocEntry = entity.SAPDocEntry,
            SAPDocNum = entity.SAPDocNum,
            QuotationNumber = entity.QuotationNumber,
            QuotationDate = entity.QuotationDate,
            ValidUntil = entity.ValidUntil,
            CardCode = entity.CardCode,
            CardName = entity.CardName,
            CustomerRefNo = entity.CustomerRefNo,
            ContactPerson = entity.ContactPerson,
            Status = entity.Status,
            Comments = entity.Comments,
            TermsAndConditions = entity.TermsAndConditions,
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
            SalesOrderId = entity.SalesOrderId,
            IsSynced = entity.IsSynced,
            Lines = entity.Lines.Select(l => new QuotationLineDto
            {
                Id = l.Id,
                LineNum = l.LineNum,
                ItemCode = l.ItemCode,
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPercent,
                TaxPercent = l.TaxPercent,
                LineTotal = l.LineTotal,
                WarehouseCode = l.WarehouseCode,
                UoMCode = l.UoMCode
            }).ToList()
        };
    }

    #endregion
}
