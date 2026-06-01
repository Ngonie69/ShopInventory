using System.Text.Json.Serialization;

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
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal InvoiceAmount { get; set; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public decimal InvoiceTaxAmount { get; set; }
    public string? Istatus { get; set; }
    public string? Cashier { get; set; }
    public string? InvoiceComment { get; set; }
    public object? ItemsXml { get; set; }
    public object? CurrenciesXml { get; set; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public int? refDeviceId { get; set; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long? refReceiptGlobalNo { get; set; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public int? refFiscalDayNo { get; set; }
}