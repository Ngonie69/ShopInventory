namespace ShopInventory.DTOs;

/// <summary>
/// One document sent, or waiting to be sent, to one WhatsApp number.
/// </summary>
/// <remarks>
/// The recipient travels masked: the history is read by people who may read invoices but do not keep
/// the register of numbers. Statuses travel as names.
/// </remarks>
public sealed class CustomerDocumentDeliveryDto
{
    public long Id { get; set; }

    /// <summary>SapInvoice or SaleReceipt.</summary>
    public string DocumentType { get; set; } = string.Empty;

    public int? SapDocEntry { get; set; }

    public int? SapDocNum { get; set; }

    public int? DesktopSaleId { get; set; }

    public string DocumentNumber { get; set; } = string.Empty;

    public DateTime? DocumentDate { get; set; }

    /// <summary>The total the customer reads — the document's own currency.</summary>
    public decimal? DocumentTotal { get; set; }

    public string? Currency { get; set; }

    public string? CardCode { get; set; }

    public string? CardName { get; set; }

    public string? RouteCustomerName { get; set; }

    public int? ContactId { get; set; }

    public string RecipientMasked { get; set; } = string.Empty;

    public string? RecipientName { get; set; }

    /// <summary>Auto, Manual or Counter.</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// Pending, Preparing, WaitingForFiscal, Sending, Sent, SentUnconfirmed, Uncertain, NotOnWhatsApp,
    /// Held, Failed, Cancelled or Skipped.
    /// </summary>
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

    /// <summary>Which record vouched for the fiscal receipt printed on it.</summary>
    public string? FiscalEvidenceSource { get; set; }

    public long? SupersedesDeliveryId { get; set; }

    /// <summary>Whether a person may send it again.</summary>
    public bool CanRetry { get; set; }

    /// <summary>Whether sending it again needs the sender to confirm the customer did not get it.</summary>
    public bool RetryNeedsConfirmation { get; set; }

    /// <summary>Whether it can still be withdrawn.</summary>
    public bool CanCancel { get; set; }
}
