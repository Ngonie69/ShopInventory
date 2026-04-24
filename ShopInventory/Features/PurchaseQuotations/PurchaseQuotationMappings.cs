using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.PurchaseQuotations;

internal static class PurchaseQuotationMappings
{
    public static PurchaseQuotationDto MapFromSap(SAPPurchaseQuotation sapQuotation)
    {
        var lines = sapQuotation.DocumentLines?.Select(line => new PurchaseQuotationLineDto
        {
            LineNum = line.LineNum,
            ItemCode = line.ItemCode,
            ItemDescription = line.ItemDescription,
            Quantity = line.Quantity ?? 0,
            UnitPrice = line.UnitPrice ?? 0,
            LineTotal = line.LineTotal ?? 0,
            WarehouseCode = line.WarehouseCode,
            DiscountPercent = line.DiscountPercent ?? 0,
            TaxCode = line.TaxCode,
            UoMCode = line.UoMCode
        }).ToList();

        return new PurchaseQuotationDto
        {
            DocEntry = sapQuotation.DocEntry,
            DocNum = sapQuotation.DocNum,
            DocDate = sapQuotation.DocDate,
            DocDueDate = sapQuotation.DocDueDate,
            CardCode = sapQuotation.CardCode,
            CardName = sapQuotation.CardName,
            NumAtCard = sapQuotation.NumAtCard,
            Comments = sapQuotation.Comments,
            DocStatus = MapStatus(sapQuotation.DocumentStatus, sapQuotation.Cancelled),
            DocTotal = sapQuotation.DocTotal ?? 0,
            VatSum = sapQuotation.VatSum ?? 0,
            DiscountPercent = sapQuotation.DiscountPercent ?? 0,
            TotalDiscount = sapQuotation.TotalDiscount ?? 0,
            DocCurrency = sapQuotation.DocCurrency,
            BillToAddress = sapQuotation.Address,
            ShipToAddress = sapQuotation.Address2,
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