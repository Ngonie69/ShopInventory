using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// Turns one already-fiscalised sale into the SAP A/R invoice that records it.
///
/// Shared by the van route and the till route, which post from the same table and want the same
/// document: one invoice per sale, keyed on the sale's own reference so a retry, a mop-up and a manual
/// re-run all collapse onto it rather than creating a second.
/// </summary>
public static class DesktopSaleInvoiceRequestBuilder
{
    public static CreateInvoiceRequest Build(DesktopSaleEntity sale, string? comments = null) => new()
    {
        CardCode = sale.CardCode,
        DocDate = sale.DocDate.ToString("yyyy-MM-dd"),
        DocDueDate = sale.DocDate.ToString("yyyy-MM-dd"),
        NumAtCard = sale.ExternalReferenceId,
        // Both the SAP-side duplicate guard and the local idempotency key. Set from the sale's own
        // reference, which is derivable without anything the post returns — the property that makes
        // recovery after a lost reply possible at all.
        U_Van_saleorder = sale.ExternalReferenceId,
        ClientRequestId = sale.ExternalReferenceId,
        // The same string again, into the mobile invoice-number UDF when one is configured. It looks
        // redundant beside U_Van_saleorder and is not: this one is also what the fiscalisation platform
        // is told the receipt's invoice number is, so it is what makes a receipt stamped on the handset
        // and a receipt raised from this SAP invoice the same document to ZIMRA rather than two.
        MobileInvoiceNumber = sale.ExternalReferenceId,
        DocCurrency = sale.Currency,
        Comments = comments,
        Lines = sale.Lines
            .OrderBy(l => l.LineNum)
            .Select(l => new CreateInvoiceLineRequest
            {
                ItemCode = l.ItemCode,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                WarehouseCode = string.IsNullOrWhiteSpace(l.WarehouseCode) ? sale.WarehouseCode : l.WarehouseCode,
                TaxCode = l.TaxCode,
                DiscountPercent = l.DiscountPercent,
                UoMCode = l.UoMCode,
                // From the header: DesktopSaleLineEntity.CostCentreCode is not mapped, so a re-read
                // sale always has it null on the line.
                CostCentreCode = sale.CostCentreCode,
                // FEFO server-side. Neither a handset nor a till chooses batches — both sell by item.
                AutoAllocateBatches = true,
                BatchNumbers = null
            })
            .ToList()
    };
}
