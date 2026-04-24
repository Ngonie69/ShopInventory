using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.GoodsReceiptPurchaseOrders;

internal static class GoodsReceiptPurchaseOrderMappings
{
    public static GoodsReceiptPurchaseOrderDto MapFromSap(SAPGoodsReceiptPurchaseOrder sapGoodsReceipt)
    {
        var lines = sapGoodsReceipt.DocumentLines?.Select(line => new GoodsReceiptPurchaseOrderLineDto
        {
            LineNum = line.LineNum,
            ItemCode = line.ItemCode,
            ItemDescription = line.ItemDescription,
            Quantity = line.Quantity ?? 0,
            LineTotal = line.LineTotal ?? 0,
            WarehouseCode = line.WarehouseCode,
            TaxCode = line.TaxCode,
            UoMCode = line.UoMCode,
            BaseEntry = line.BaseEntry,
            BaseLine = line.BaseLine,
            BaseType = line.BaseType
        }).ToList();

        return new GoodsReceiptPurchaseOrderDto
        {
            DocEntry = sapGoodsReceipt.DocEntry,
            DocNum = sapGoodsReceipt.DocNum,
            DocDate = sapGoodsReceipt.DocDate,
            DocDueDate = sapGoodsReceipt.DocDueDate,
            CardCode = sapGoodsReceipt.CardCode,
            CardName = sapGoodsReceipt.CardName,
            NumAtCard = sapGoodsReceipt.NumAtCard,
            Comments = sapGoodsReceipt.Comments,
            DocStatus = MapStatus(sapGoodsReceipt.DocumentStatus, sapGoodsReceipt.Cancelled),
            DocTotal = sapGoodsReceipt.DocTotal ?? 0,
            VatSum = sapGoodsReceipt.VatSum ?? 0,
            DiscountPercent = sapGoodsReceipt.DiscountPercent ?? 0,
            TotalDiscount = sapGoodsReceipt.TotalDiscount ?? 0,
            DocCurrency = sapGoodsReceipt.DocCurrency,
            BillToAddress = sapGoodsReceipt.Address,
            ShipToAddress = sapGoodsReceipt.Address2,
            Source = "SAP",
            Lines = lines
        };
    }

    private static string MapStatus(string? documentStatus, string? cancelled)
    {
        if (string.Equals(cancelled, "tYES", StringComparison.OrdinalIgnoreCase))
            return "Cancelled";

        return string.Equals(documentStatus, "bost_Close", StringComparison.OrdinalIgnoreCase)
            ? "Closed"
            : "Open";
    }
}