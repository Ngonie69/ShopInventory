using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.PurchaseInvoices;

internal static class PurchaseInvoiceMappings
{
    public static PurchaseInvoiceDto MapFromSap(SAPPurchaseInvoice sapInvoice)
    {
        var lines = sapInvoice.DocumentLines?.Select(line => new PurchaseInvoiceLineDto
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

        return new PurchaseInvoiceDto
        {
            DocEntry = sapInvoice.DocEntry,
            DocNum = sapInvoice.DocNum,
            DocDate = sapInvoice.DocDate,
            DocDueDate = sapInvoice.DocDueDate,
            CardCode = sapInvoice.CardCode,
            CardName = sapInvoice.CardName,
            NumAtCard = sapInvoice.NumAtCard,
            Comments = sapInvoice.Comments,
            DocStatus = MapStatus(sapInvoice.DocumentStatus, sapInvoice.Cancelled),
            DocTotal = sapInvoice.DocTotal ?? 0,
            VatSum = sapInvoice.VatSum ?? 0,
            DiscountPercent = sapInvoice.DiscountPercent ?? 0,
            TotalDiscount = sapInvoice.TotalDiscount ?? 0,
            DocCurrency = sapInvoice.DocCurrency,
            BillToAddress = sapInvoice.Address,
            ShipToAddress = sapInvoice.Address2,
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