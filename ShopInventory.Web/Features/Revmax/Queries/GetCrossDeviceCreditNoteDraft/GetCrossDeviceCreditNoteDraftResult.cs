using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Revmax.Queries.GetCrossDeviceCreditNoteDraft;

public sealed class GetCrossDeviceCreditNoteDraftResult
{
    public CreditNoteDto CreditNote { get; init; } = null!;
    public InvoiceDto? OriginalInvoice { get; init; }
    public RevmaxInvoiceResponse? SourceFiscalInvoice { get; init; }
    public RevmaxCardDetailsResponse? CurrentDevice { get; init; }
    public string? CreditNoteFiscalInvoiceNumber { get; init; }
    public string? SourceInvoiceNumber { get; init; }
    public string? SourceFiscalInvoiceError { get; init; }
    public string? CurrentDeviceError { get; init; }
    public int? SuggestedRefDeviceId { get; init; }
    public long? SuggestedRefReceiptGlobalNo { get; init; }
    public int? SuggestedRefFiscalDayNo { get; init; }
    public string? SuggestedCurrency { get; init; }
    public string? SuggestedBranchName { get; init; }
    public string? SuggestedCustomerName { get; init; }
    public string? SuggestedCustomerVatNumber { get; init; }
    public string? SuggestedCustomerAddress { get; init; }
    public string? SuggestedCustomerTelephone { get; init; }
    public string? SuggestedCustomerEmail { get; init; }
    public string? SuggestedCustomerBpn { get; init; }
    public string? SuggestedInvoiceComment { get; init; }
}