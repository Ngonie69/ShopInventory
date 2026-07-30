using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.AppVersion;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Merchandiser.Queries.GetMobileOrderByClientRequestId;

/// <summary>
/// Resolves the order a device's idempotency key produced. The mobile app needs this because a
/// submit can succeed on the server and still leave the device without the order: a request that
/// times out, or a retry that the idempotency middleware replays with a bare message body instead
/// of the order. Without a lookup the rep is left holding a draft that looks unsent, and the only
/// way out is to key the order again — which is exactly the duplicate the key existed to prevent.
/// </summary>
public sealed class GetMobileOrderByClientRequestIdHandler(
    ApplicationDbContext context,
    MobileOrderStatusCompatibilityService statusCompatibilityService
) : IRequestHandler<GetMobileOrderByClientRequestIdQuery, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        GetMobileOrderByClientRequestIdQuery request,
        CancellationToken cancellationToken)
    {
        var clientRequestId = request.ClientRequestId?.Trim();
        if (string.IsNullOrWhiteSpace(clientRequestId))
            return Errors.SalesOrder.InvalidOperation("A client request ID is required.");

        var order = await context.SalesOrders
            .AsNoTracking()
            .Where(o => o.Source == SalesOrderSource.Mobile)
            .Where(o => o.CreatedByUserId == request.UserId)
            .Where(o => o.ClientRequestId == clientRequestId)
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
                Currency = o.Currency,
                SubTotal = o.SubTotal,
                TaxAmount = o.TaxAmount,
                DiscountPercent = o.DiscountPercent,
                DiscountAmount = o.DiscountAmount,
                DocTotal = o.DocTotal,
                WarehouseCode = o.WarehouseCode,
                CreatedByUserId = o.CreatedByUserId,
                CreatedAt = o.CreatedAt,
                UpdatedAt = o.UpdatedAt,
                InvoiceId = o.InvoiceId,
                IsSynced = o.IsSynced && o.SAPDocNum.HasValue && o.SAPDocNum > 0,
                SyncError = o.SyncError,
                Source = o.Source,
                ClientRequestId = o.ClientRequestId,
                MerchandiserNotes = o.MerchandiserNotes,
                Latitude = o.Latitude,
                Longitude = o.Longitude,
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
            .FirstOrDefaultAsync(cancellationToken);

        if (order is null)
            return Errors.SalesOrder.NotFoundByClientRequestId(clientRequestId);

        order.Status = statusCompatibilityService.GetVisibleMobileStatus(order.Status, order.IsSynced, order.SAPDocNum);
        return order;
    }
}
