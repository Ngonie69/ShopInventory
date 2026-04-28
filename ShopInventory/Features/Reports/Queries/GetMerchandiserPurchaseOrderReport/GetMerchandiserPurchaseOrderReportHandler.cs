using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Reports.Queries.GetMerchandiserPurchaseOrderReport;

public sealed class GetMerchandiserPurchaseOrderReportHandler(
    ApplicationDbContext context,
    ILogger<GetMerchandiserPurchaseOrderReportHandler> logger
) : IRequestHandler<GetMerchandiserPurchaseOrderReportQuery, ErrorOr<MerchandiserPurchaseOrderReportDto>>
{
    public async Task<ErrorOr<MerchandiserPurchaseOrderReportDto>> Handle(
        GetMerchandiserPurchaseOrderReportQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var fromDate = NormalizeUtcDate(request.FromDate);
            var toExclusive = NormalizeUtcDate(request.ToDate)?.AddDays(1);
            var search = request.Search?.Trim();

            var ordersQuery = context.SalesOrders
                .AsNoTracking()
                .Where(order => order.Source == SalesOrderSource.Mobile)
                .Where(order => order.CreatedByUserId.HasValue)
                .Where(order => order.CreatedByUser != null && order.CreatedByUser.Role == "Merchandiser");

            if (fromDate.HasValue)
            {
                ordersQuery = ordersQuery.Where(order => order.CreatedAt >= fromDate.Value);
            }

            if (toExclusive.HasValue)
            {
                ordersQuery = ordersQuery.Where(order => order.CreatedAt < toExclusive.Value);
            }

            if (request.MerchandiserUserId.HasValue)
            {
                ordersQuery = ordersQuery.Where(order => order.CreatedByUserId == request.MerchandiserUserId.Value);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var searchPattern = $"%{search}%";
                var numericSearch = int.TryParse(search, out var numericValue) ? numericValue : (int?)null;

                ordersQuery = ordersQuery.Where(order =>
                    EF.Functions.ILike(order.OrderNumber, searchPattern) ||
                    (order.CustomerRefNo != null && EF.Functions.ILike(order.CustomerRefNo, searchPattern)) ||
                    EF.Functions.ILike(order.CardCode, searchPattern) ||
                    (order.CardName != null && EF.Functions.ILike(order.CardName, searchPattern)) ||
                    (order.CreatedByUser != null &&
                        (EF.Functions.ILike(order.CreatedByUser.Username, searchPattern) ||
                         (order.CreatedByUser.FirstName != null && EF.Functions.ILike(order.CreatedByUser.FirstName, searchPattern)) ||
                         (order.CreatedByUser.LastName != null && EF.Functions.ILike(order.CreatedByUser.LastName, searchPattern)))) ||
                    (numericSearch.HasValue &&
                        (order.SAPDocNum == numericSearch.Value || order.SAPDocEntry == numericSearch.Value)));
            }

            var orderRows = await ordersQuery
                .OrderByDescending(order => order.CreatedAt)
                .Select(order => new
                {
                    order.Id,
                    order.OrderNumber,
                    order.OrderDate,
                    order.CreatedAt,
                    order.CardCode,
                    order.CardName,
                    order.CustomerRefNo,
                    order.Status,
                    order.SAPDocEntry,
                    order.SAPDocNum,
                    order.IsSynced,
                    order.WarehouseCode,
                    order.DocTotal,
                    order.Currency,
                    order.MerchandiserNotes,
                    MerchandiserUserId = order.CreatedByUserId!.Value,
                    MerchandiserUsername = order.CreatedByUser!.Username,
                    MerchandiserFirstName = order.CreatedByUser.FirstName,
                    MerchandiserLastName = order.CreatedByUser.LastName,
                    ItemCount = order.Lines.Count(),
                    TotalQuantity = order.Lines.Sum(line => (decimal?)line.Quantity) ?? 0m
                })
                .ToListAsync(cancellationToken);

            if (orderRows.Count == 0)
            {
                return CreateReport(request, new List<MerchandiserPurchaseOrderReportOrderDto>());
            }

            var orderIds = orderRows.Select(order => order.Id).ToList();
            var attachmentReferences = orderRows
                .Select(order => ResolveAttachmentReference(order.CustomerRefNo, order.OrderNumber))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var lineRows = await context.SalesOrders
                .AsNoTracking()
                .Where(order => orderIds.Contains(order.Id))
                .SelectMany(order => order.Lines.Select(line => new
                {
                    OrderId = order.Id,
                    Line = new MerchandiserPurchaseOrderReportLineDto
                    {
                        LineNum = line.LineNum,
                        ItemCode = line.ItemCode,
                        ItemDescription = line.ItemDescription,
                        Quantity = line.Quantity,
                        QuantityFulfilled = line.QuantityFulfilled,
                        UnitPrice = line.UnitPrice,
                        LineTotal = line.LineTotal,
                        WarehouseCode = line.WarehouseCode
                    }
                }))
                .ToListAsync(cancellationToken);

            var attachmentRows = await context.DocumentAttachments
                .AsNoTracking()
                .Where(attachment => attachment.EntityType == "ExternalPurchaseOrder")
                .Where(attachment => attachment.ExternalReference != null && attachmentReferences.Contains(attachment.ExternalReference))
                .OrderByDescending(attachment => attachment.UploadedAt)
                .Select(attachment => new
                {
                    ExternalReference = attachment.ExternalReference!,
                    Attachment = new MerchandiserPurchaseOrderReportAttachmentDto
                    {
                        AttachmentId = attachment.Id,
                        FileName = attachment.FileName,
                        MimeType = attachment.MimeType,
                        FileSizeBytes = attachment.FileSizeBytes,
                        Description = attachment.Description,
                        UploadedAtUtc = attachment.UploadedAt,
                        UploadedByUsername = attachment.UploadedByUser != null ? attachment.UploadedByUser.Username : null
                    }
                })
                .ToListAsync(cancellationToken);

            var linesByOrderId = lineRows
                .GroupBy(line => line.OrderId)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(line => line.Line)
                        .OrderBy(line => line.LineNum)
                        .ToList());

            var attachmentsByReference = attachmentRows
                .GroupBy(attachment => attachment.ExternalReference, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(attachment => attachment.Attachment)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var orders = orderRows
                .Select(order =>
                {
                    var attachmentReference = ResolveAttachmentReference(order.CustomerRefNo, order.OrderNumber);
                    var attachments = attachmentsByReference.TryGetValue(attachmentReference, out var attachmentGroup)
                        ? attachmentGroup
                        : new List<MerchandiserPurchaseOrderReportAttachmentDto>();
                    var lines = linesByOrderId.TryGetValue(order.Id, out var lineGroup)
                        ? lineGroup
                        : new List<MerchandiserPurchaseOrderReportLineDto>();

                    return new MerchandiserPurchaseOrderReportOrderDto
                    {
                        SalesOrderId = order.Id,
                        OrderNumber = order.OrderNumber,
                        AttachmentReference = attachmentReference,
                        OrderDateUtc = order.OrderDate,
                        CreatedAtUtc = order.CreatedAt,
                        CardCode = order.CardCode,
                        CardName = order.CardName,
                        CustomerRefNo = order.CustomerRefNo,
                        Status = order.Status,
                        SapDocEntry = order.SAPDocEntry,
                        SapDocNum = order.SAPDocNum,
                        IsSynced = order.IsSynced,
                        WarehouseCode = order.WarehouseCode,
                        DocTotal = order.DocTotal,
                        Currency = order.Currency,
                        MerchandiserUserId = order.MerchandiserUserId,
                        MerchandiserUsername = order.MerchandiserUsername,
                        MerchandiserFullName = BuildFullName(order.MerchandiserFirstName, order.MerchandiserLastName, order.MerchandiserUsername),
                        MerchandiserNotes = order.MerchandiserNotes,
                        ItemCount = order.ItemCount,
                        TotalQuantity = order.TotalQuantity,
                        HasAttachments = attachments.Count > 0,
                        AttachmentCount = attachments.Count,
                        Attachments = attachments,
                        Lines = lines
                    };
                })
                .ToList();

            if (request.HasAttachments.HasValue)
            {
                orders = orders
                    .Where(order => order.HasAttachments == request.HasAttachments.Value)
                    .ToList();
            }

            return CreateReport(request, orders);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to generate merchandiser purchase order report");
            return Errors.Report.GenerationFailed("Failed to load merchandiser purchase order report.");
        }
    }

    private static MerchandiserPurchaseOrderReportDto CreateReport(
        GetMerchandiserPurchaseOrderReportQuery request,
        List<MerchandiserPurchaseOrderReportOrderDto> orders)
    {
        var merchandisers = orders
            .GroupBy(order => new
            {
                order.MerchandiserUserId,
                order.MerchandiserUsername,
                order.MerchandiserFullName
            })
            .Select(group => new MerchandiserPurchaseOrderReportMerchandiserDto
            {
                MerchandiserUserId = group.Key.MerchandiserUserId,
                Username = group.Key.MerchandiserUsername,
                FullName = group.Key.MerchandiserFullName,
                OrderCount = group.Count(),
                OrdersWithAttachments = group.Count(order => order.HasAttachments),
                AttachmentCount = group.Sum(order => order.AttachmentCount),
                SyncedOrders = group.Count(order => order.IsSynced),
                TotalOrderValue = group.Sum(order => order.DocTotal),
                LatestOrderCreatedAtUtc = group.Max(order => order.CreatedAtUtc)
            })
            .OrderByDescending(merchandiser => merchandiser.OrderCount)
            .ThenBy(merchandiser => merchandiser.Username)
            .ToList();

        return new MerchandiserPurchaseOrderReportDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            FromDate = request.FromDate,
            ToDate = request.ToDate,
            MerchandiserUserId = request.MerchandiserUserId,
            HasAttachments = request.HasAttachments,
            Search = request.Search,
            TotalMerchandisers = merchandisers.Count,
            TotalOrders = orders.Count,
            OrdersWithAttachments = orders.Count(order => order.HasAttachments),
            OrdersWithoutAttachments = orders.Count(order => !order.HasAttachments),
            SyncedOrders = orders.Count(order => order.IsSynced),
            UnsyncedOrders = orders.Count(order => !order.IsSynced),
            TotalAttachments = orders.Sum(order => order.AttachmentCount),
            TotalOrderValue = orders.Sum(order => order.DocTotal),
            Merchandisers = merchandisers,
            Orders = orders
        };
    }

    private static string ResolveAttachmentReference(string? customerRefNo, string orderNumber) =>
        string.IsNullOrWhiteSpace(customerRefNo) ? orderNumber : customerRefNo.Trim();

    private static DateTime? NormalizeUtcDate(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);
    }

    private static string BuildFullName(string? firstName, string? lastName, string username)
    {
        var fullName = string.Join(" ", new[] { firstName, lastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        return string.IsNullOrWhiteSpace(fullName) ? username : fullName;
    }
}