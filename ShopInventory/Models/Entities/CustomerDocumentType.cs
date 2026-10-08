namespace ShopInventory.Models.Entities;

/// <summary>What a customer document delivery sends.</summary>
public enum CustomerDocumentType
{
    /// <summary>A SAP A/R invoice, rendered as the Fiscal Tax Invoice PDF.</summary>
    SapInvoice = 0,

    /// <summary>A till sale's own fiscal receipt, rendered before the sale reaches SAP.</summary>
    SaleReceipt = 1
}
