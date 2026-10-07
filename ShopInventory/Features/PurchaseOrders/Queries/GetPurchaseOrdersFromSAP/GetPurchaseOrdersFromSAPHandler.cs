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
        // Filtered, counted and paged by SAP. The page used to ask for up to ten thousand orders with their
        // lines, and with a date or supplier this read every match and paged it here.
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Max(1, request.PageSize);

        try
        {
            string? statusFilter = null;
            if (request.Status is { } status && !PurchaseOrderSapStatus.TryGetFilter(status, out statusFilter))
            {
                // Draft, pending, on hold and partially received are local states; SAP holds none of them.
                return new PurchaseOrderListResponseDto
                {
                    Page = page,
                    PageSize = pageSize,
                    Summary = request.IncludeSummary ? new PurchaseOrderListSummaryDto() : null
                };
            }

            var totalCount = await sapClient.CountPurchaseOrdersAsync(
                request.CardCode, request.FromDate, request.ToDate, statusFilter, cancellationToken);

            var sapOrders = totalCount <= (page - 1) * pageSize
                ? new List<SAPPurchaseOrder>()
                : await sapClient.GetPurchaseOrderPageAsync(
                    request.CardCode, request.FromDate, request.ToDate, statusFilter, (page - 1) * pageSize, pageSize, cancellationToken);

            return new PurchaseOrderListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
                Orders = sapOrders.Select(MapSAPToPurchaseOrderDto).ToList(),
                Summary = request.IncludeSummary ? await SummarizeAsync(request, totalCount, cancellationToken) : null
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching purchase orders from SAP");
            return Errors.PurchaseOrder.SapError(ex.Message);
        }
    }

    /// <summary>
    /// The figures for the matching orders. SAP's orders read as Approved, Received or Cancelled only,
    /// so Draft and Pending are none; with a status filter only that status has any.
    /// </summary>
    private async Task<PurchaseOrderListSummaryDto> SummarizeAsync(
        GetPurchaseOrdersFromSAPQuery request, int totalCount, CancellationToken cancellationToken)
    {
        return new PurchaseOrderListSummaryDto
        {
            Total = totalCount,
            Approved = await CountAsync(PurchaseOrderStatus.Approved),
            Received = await CountAsync(PurchaseOrderStatus.Received)
        };

        async Task<int> CountAsync(PurchaseOrderStatus status)
        {
            if (request.Status.HasValue)
                return request.Status == status ? totalCount : 0;

            PurchaseOrderSapStatus.TryGetFilter(status, out var filter);
            return await sapClient.CountPurchaseOrdersAsync(
                request.CardCode, request.FromDate, request.ToDate, filter, cancellationToken);
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

/// <summary>
/// The SAP condition for each status <c>MapSAPToPurchaseOrderDto</c> gives a SAP order: cancelled first,
/// then closed (received), else open (approved). A cancelled order is closed too, so the other two
/// exclude it.
/// </summary>
public static class PurchaseOrderSapStatus
{
    public static bool TryGetFilter(PurchaseOrderStatus status, out string? filter)
    {
        filter = status switch
        {
            PurchaseOrderStatus.Cancelled => "Cancelled eq 'tYES'",
            PurchaseOrderStatus.Received => "DocumentStatus eq 'bost_Close' and Cancelled eq 'tNO'",
            PurchaseOrderStatus.Approved => "DocumentStatus eq 'bost_Open' and Cancelled eq 'tNO'",
            _ => null
        };

        return filter is not null;
    }
}
