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
                AutoAllocateBatches = true,
                //
                // Left unset here, and filled in before the post rather than never.
                //
                // DesktopSaleLineEntity carries no batch, because neither a handset nor a till
                // chooses one — both sell by item. This request used to go as it is, on the reasoning
                // that no selection at least left the decision to SAP's own item settings. It does
                // not: the first till sales to reach a live company one-to-one were answered with
                // "Cannot add row without complete selection of batch/serial numbers", and SAP
                // refuses the whole document, not the line. There is no configuration under which a
                // batch-managed line posts without naming its batches.
                //
                // So both callers run InvoiceBatchAllocation.AllocateAsync over this request before
                // issuing it — which is what the 18:00 consolidation has always done with these very
                // same sale lines, and the reason its documents posted where these did not. That is
                // also why AutoAllocateBatches stays true: it is the flag the allocator is asked
                // for, and false would mean "refuse a batch-managed item unless the caller names
                // batches", which no caller here can.
                BatchNumbers = null
            })
            .ToList()
    };
}
