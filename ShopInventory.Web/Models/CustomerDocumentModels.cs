namespace ShopInventory.Web.Models;

// Hand-written mirrors of the API's customer-document DTOs (ShopInventory/DTOs). Keep every value
// type's nullability as the API declares it: a nullable on the API read into a plain value here
// throws during deserialisation and loses the whole response, which the page then shows as "no data".
// Statuses and sources travel as names, so a reordered enum on either side cannot relabel them.

/// <summary>A WhatsApp number on a customer, with the consent it was saved under.</summary>
public sealed class CustomerWhatsAppContactModel
{
    public int Id { get; set; }
    public string? CardCode { get; set; }
    public int? RouteCustomerId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public string PhoneE164 { get; set; } = string.Empty;
    public string PhoneMasked { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public bool AutoSendInvoices { get; set; }
    public string ConsentSource { get; set; } = string.Empty;
    public string? ConsentNote { get; set; }
    public DateTime ConsentRecordedAtUtc { get; set; }
    public string ConsentRecordedBy { get; set; } = string.Empty;
    public bool IsOptedOut { get; set; }
    public DateTime? OptedOutAtUtc { get; set; }
    public string? OptedOutSource { get; set; }
    public string? OptedOutBy { get; set; }
    public bool? WhatsAppExists { get; set; }
    public DateTime? WhatsAppCheckedAtUtc { get; set; }
    public bool IsRemoved { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Save a number a customer gave for their documents.</summary>
public sealed class SaveCustomerWhatsAppContactModel
{
    public string? CardCode { get; set; }
    public int? RouteCustomerId { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public bool AutoSendInvoices { get; set; } = true;
    public bool ConsentConfirmed { get; set; }
    public string? ConsentNote { get; set; }
    public List<string>? AlsoApplyToCardCodes { get; set; }
    public string? OwnerName { get; set; }
}

public sealed class UpdateCustomerWhatsAppContactModel
{
    public string? ContactName { get; set; }
    public bool AutoSendInvoices { get; set; }
}

/// <summary>One document sent, or waiting to be sent, to one WhatsApp number. The number is masked.</summary>
public sealed class CustomerDocumentDeliveryModel
{
    public long Id { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public int? DesktopSaleId { get; set; }
    public string DocumentNumber { get; set; } = string.Empty;
    public DateTime? DocumentDate { get; set; }
    public decimal? DocumentTotal { get; set; }
    public string? Currency { get; set; }
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public string? RouteCustomerName { get; set; }
    public int? ContactId { get; set; }
    public string RecipientMasked { get; set; } = string.Empty;
    public string? RecipientName { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? StatusReason { get; set; }
    public string? LastError { get; set; }
    public string? RequestedBy { get; set; }
    public int DispatchAttempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SendIssuedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public string? ClosedBy { get; set; }
    public string? FileName { get; set; }
    public string? FiscalEvidenceSource { get; set; }
    public long? SupersedesDeliveryId { get; set; }
    public bool CanRetry { get; set; }
    public bool RetryNeedsConfirmation { get; set; }
    public bool CanCancel { get; set; }
}

public sealed class CustomerDocumentDeliveryPageModel
{
    public List<CustomerDocumentDeliveryModel> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>Send an invoice to saved numbers, to one typed for this send, or both.</summary>
public sealed class RequestInvoiceWhatsAppModel
{
    public List<int>? ContactIds { get; set; }
    public string? OneOffPhone { get; set; }
    public string? OneOffName { get; set; }
    public bool ConsentAffirmed { get; set; }
    public bool SaveAsContact { get; set; }
    public bool AutoSendFutureInvoices { get; set; }
}

public sealed class RetryCustomerDocumentDeliveryModel
{
    public bool ConfirmNotReceived { get; set; }
}

public sealed class WhatsAppSessionOptionModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Phone { get; set; }
}

public sealed class CustomerDocumentDeliveryStatusModel
{
    public bool Enabled { get; set; }
    public bool GatewayConfigured { get; set; }
    public bool AutoSendEnabled { get; set; }
    public string? SessionId { get; set; }
    public string? SessionName { get; set; }
    public string? SessionStatus { get; set; }
    public string? SessionPhone { get; set; }
    public string? SessionError { get; set; }
    public List<WhatsAppSessionOptionModel> Sessions { get; set; } = [];
    public int SentToday { get; set; }
    public int SentLastHour { get; set; }
    public int AutoSentToday { get; set; }
    public int MaxAutoPerDay { get; set; }
    public int MaxPerHour { get; set; }
    public int HardMaxPerDay { get; set; }
    public string AutoWindow { get; set; } = string.Empty;
    public bool WithinAutoWindow { get; set; }
    public int Waiting { get; set; }
    public int WaitingForFiscal { get; set; }
    public int Held { get; set; }
    public int Uncertain { get; set; }
    public int FailedToday { get; set; }
    public DateTime? LastSentAtUtc { get; set; }
    public string? SettingsChangedBy { get; set; }
    public DateTime? SettingsChangedAtUtc { get; set; }
}

public sealed class UpdateCustomerDocumentDeliverySettingsModel
{
    public bool AutoSendEnabled { get; set; }
    public string? WhatsAppSessionId { get; set; }
    public int MaxAutoPerDay { get; set; }
}

public sealed class WhatsAppNumberCheckResultModel
{
    public string Input { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public string? PhoneE164 { get; set; }
    public string? PhoneMasked { get; set; }
    public bool OptedOut { get; set; }
    public int? ExistingContactId { get; set; }
    public string? Message { get; set; }
}

public sealed class InvoiceWhatsAppPreviewModel
{
    public bool Ready { get; set; }
    public string? Reason { get; set; }
    public string? FileName { get; set; }
    public string? Caption { get; set; }
    public string? FiscalEvidenceSource { get; set; }
    public string? PdfBase64 { get; set; }
}
