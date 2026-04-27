using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Merchandiser.Queries.GetMobileOrderById;

public sealed class GetMobileOrderByIdHandler(
    ApplicationDbContext context
) : IRequestHandler<GetMobileOrderByIdQuery, ErrorOr<SalesOrderDto>>
{
    public async Task<ErrorOr<SalesOrderDto>> Handle(
        GetMobileOrderByIdQuery request,
        CancellationToken cancellationToken)
    {
        var order = await context.SalesOrders
            .AsNoTracking()
            .Where(o => o.Source == SalesOrderSource.Mobile)
            .Where(o => o.CreatedByUserId == request.UserId)
            .Where(o => o.Id == request.Id)
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
            .FirstOrDefaultAsync(cancellationToken);

        return order is null ? Errors.SalesOrder.NotFound(request.Id) : order;
    }
}