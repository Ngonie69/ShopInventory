using ShopInventory.Features.Invoices;

namespace ShopInventory.Features.CustomerDocuments.Documents;

/// <summary>A document ready to send: the file, its name and caption, and the receipt it carries.</summary>
public sealed record ComposedCustomerDocument(
    byte[] Bytes,
    string FileName,
    string Caption,
    string Sha256,
    InvoicePdfReceipt Receipt,
    string FiscalEvidenceSource);
