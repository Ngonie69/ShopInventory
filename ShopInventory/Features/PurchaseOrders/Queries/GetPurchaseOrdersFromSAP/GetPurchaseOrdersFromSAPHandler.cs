using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrdersFromSAP;

public sealed class GetPurchaseOrdersFromSAPHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetPurchaseOrdersFromSAPHandler> logger
) : IRequestHandler<GetPurchaseOrdersFromSAPQuery, ErrorOr<PurchaseOrderListResponseDto>>
{
    public async Task<ErrorOr<PurchaseOrderListResponseDto>> Handle(
        GetPurchaseOrdersFromSAPQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            List<SAPPurchaseOrder> sapOrders;

            if (request.FromDate.HasValue && request.ToDate.HasValue)
            {
                sapOrders = await sapClient.GetPurchaseOrdersByDateRangeAsync(request.FromDate.Value, request.ToDate.Value, cancellationToken);
            }
            else if (!string.IsNullOrEmpty(request.CardCode))
            {
                sapOrders = await sapClient.GetPurchaseOrdersBySupplierAsync(request.CardCode, cancellationToken);
            }
            else
            {
                sapOrders = await sapClient.GetPagedPurchaseOrdersFromSAPAsync(request.Page, request.PageSize, cancellationToken);
            }

            if (!string.IsNullOrEmpty(request.CardCode) && request.FromDate.HasValue)
            {
                sapOrders = sapOrders.Where(o => o.CardCode == request.CardCode).ToList();
            }

            var totalCount = sapOrders.Count;

            if (request.FromDate.HasValue || !string.IsNullOrEmpty(request.CardCode))
            {
                sapOrders = sapOrders
                    .Skip((request.Page - 1) * request.PageSize)
                    .Take(request.PageSize)
                    .ToList();
            }

            var orders = sapOrders.Select(MapSAPToPurchaseOrderDto).ToList();

            return new PurchaseOrderListResponseDto
            {
                Page = request.Page,
                PageSize = request.PageSize,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
                Orders = orders
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching purchase orders from SAP");
            return Errors.PurchaseOrder.SapError(ex.Message);
        }
    }

    private static PurchaseOrderDto MapSAPToPurchaseOrderDto(SAPPurchaseOrder sap)
    {
        var isCancelled = sap.Cancelled == "tYES";
        var isClosed = sap.DocumentStatus == "bost_Close";

        PurchaseOrderStatus status;
        if (isCancelled)
            status = PurchaseOrderStatus.Cancelled;
        else if (isClosed)
            status = PurchaseOrderStatus.Received;
        else
            status = PurchaseOrderStatus.Approved;

        DateTime.TryParse(sap.DocDate, out var orderDate);
        DateTime.TryParse(sap.DocDueDate, out var deliveryDate);

        var lines = sap.DocumentLines?.Select((l, idx) => new PurchaseOrderLineDto
        {
            Id = idx,
            LineNum = l.LineNum,
            ItemCode = l.ItemCode ?? "",
            ItemDescription = l.ItemDescription ?? "",
            Quantity = l.Quantity ?? 0,
            QuantityReceived = l.DeliveredQuantity ?? 0,
            UnitPrice = l.UnitPrice ?? 0,
            LineTotal = l.LineTotal ?? 0,
            WarehouseCode = l.WarehouseCode,
            DiscountPercent = l.DiscountPercent ?? 0,
            UoMCode = l.UoMCode
        }).ToList() ?? new List<PurchaseOrderLineDto>();

        return new PurchaseOrderDto
        {
            Id = sap.DocEntry,
            SAPDocEntry = sap.DocEntry,
            SAPDocNum = sap.DocNum,
            OrderNumber = $"SAP-{sap.DocNum}",
            OrderDate = orderDate,
            DeliveryDate = deliveryDate == default ? null : deliveryDate,
            CardCode = sap.CardCode ?? "",
            CardName = sap.CardName,
            SupplierRefNo = sap.NumAtCard,
            Status = status,
            Currency = sap.DocCurrency ?? "USD",
            SubTotal = (sap.DocTotal ?? 0) - (sap.VatSum ?? 0),
            TaxAmount = sap.VatSum ?? 0,
            DiscountAmount = sap.TotalDiscount ?? 0,
            DocTotal = sap.DocTotal ?? 0,
            Comments = sap.Comments,
            Lines = lines,
            CreatedByUserName = "SAP",
            Source = "SAP"
        };
    }
}
