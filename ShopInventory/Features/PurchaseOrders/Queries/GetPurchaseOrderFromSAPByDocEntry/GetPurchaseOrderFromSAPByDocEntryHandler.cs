using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseOrders.Queries.GetPurchaseOrderFromSAPByDocEntry;

public sealed class GetPurchaseOrderFromSAPByDocEntryHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetPurchaseOrderFromSAPByDocEntryHandler> logger
) : IRequestHandler<GetPurchaseOrderFromSAPByDocEntryQuery, ErrorOr<PurchaseOrderDto>>
{
    public async Task<ErrorOr<PurchaseOrderDto>> Handle(
        GetPurchaseOrderFromSAPByDocEntryQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var sapOrder = await sapClient.GetPurchaseOrderByDocEntryAsync(request.DocEntry, cancellationToken);
            if (sapOrder is null)
                return Errors.PurchaseOrder.NotFoundByDocEntry(request.DocEntry);

            return MapSAPToPurchaseOrderDto(sapOrder);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching purchase order {DocEntry} from SAP", request.DocEntry);
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
