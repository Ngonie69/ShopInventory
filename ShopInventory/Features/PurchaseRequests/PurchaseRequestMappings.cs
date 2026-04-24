using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.PurchaseRequests;

internal static class PurchaseRequestMappings
{
    public static PurchaseRequestDto MapFromSap(SAPPurchaseRequest sapRequest)
    {
        var lines = sapRequest.DocumentLines?.Select(line => new PurchaseRequestLineDto
        {
            LineNum = line.LineNum,
            ItemCode = line.ItemCode,
            ItemDescription = line.ItemDescription,
            Quantity = line.Quantity ?? 0,
            OpenQuantity = line.OpenQuantity ?? 0,
            WarehouseCode = line.WarehouseCode,
            LineVendor = line.LineVendor,
            RequiredDate = line.RequiredDate,
            UoMCode = line.UoMCode
        }).ToList();

        return new PurchaseRequestDto
        {
            DocEntry = sapRequest.DocEntry,
            DocNum = sapRequest.DocNum,
            DocDate = sapRequest.DocDate,
            RequriedDate = sapRequest.RequriedDate,
            Comments = sapRequest.Comments,
            Requester = sapRequest.Requester,
            RequesterName = sapRequest.RequesterName,
            DocStatus = MapStatus(sapRequest.DocumentStatus, sapRequest.Cancelled),
            DocTotal = sapRequest.DocTotal ?? 0,
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