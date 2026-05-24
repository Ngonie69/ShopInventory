namespace ShopInventory.Web.Features.Revmax.Commands.FiscalizeCrossDeviceCreditNote;

public sealed class RevmaxTransactExtApiRequest
{
    public string? Currency { get; set; }
    public string? BranchName { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? OriginalInvoiceNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerVatNumber { get; set; }
    public string? CustomerAddress { get; set; }
    public string? CustomerTelephone { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerBPN { get; set; }
    public decimal InvoiceAmount { get; set; }
    public decimal InvoiceTaxAmount { get; set; }
    public string? Istatus { get; set; }
    public string? Cashier { get; set; }
    public string? InvoiceComment { get; set; }
    public string? ItemsXml { get; set; }
    public string? CurrenciesXml { get; set; }
    public int? refDeviceId { get; set; }
    public long? refReceiptGlobalNo { get; set; }
    public int? refFiscalDayNo { get; set; }
}