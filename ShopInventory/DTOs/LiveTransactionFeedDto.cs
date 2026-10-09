namespace ShopInventory.DTOs;

/// <summary>
/// One page of the live transactions feed: this system's sales, invoices and payments, and the
/// Fiscalisation platform's receipts, failures and fiscal days, merged into one timeline.
/// </summary>
/// <remarks>
/// Oldest first. A reader advances to the last event's <see cref="LiveTransactionEventDto.OccurredAtUtc"/>
/// and asks again; it should ask from a little before that, because a row stamped earlier can commit
/// later, and de-duplicate on <see cref="LiveTransactionEventDto.EventId"/>.
/// </remarks>
public sealed class LiveTransactionFeedDto
{
    public DateTime ServerTimeUtc { get; set; }

    /// <summary>
    /// The page stopped short. Everything up to the last event is complete; ask again from there.
    /// </summary>
    public bool HasMore { get; set; }

    /// <summary>
    /// Whether the Fiscalisation platform answered. When it did not, the local events are still
    /// complete and <see cref="FiscalFeedMessage"/> says why the fiscal ones are missing.
    /// </summary>
    public bool FiscalFeedAvailable { get; set; }

    public string? FiscalFeedMessage { get; set; }

    public List<LiveTransactionEventDto> Events { get; set; } = [];
}

public sealed class LiveTransactionEventDto
{
    /// <summary>
    /// Unique per event. A row that changes later — a payment completing, a fiscal retry — produces a new
    /// id, so the change shows as its own event rather than silently rewriting an old one.
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>See <see cref="LiveTransactionKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    public DateTime OccurredAtUtc { get; set; }

    /// <summary>The document number people recognise: sale reference, SAP DocNum or fiscal invoice number.</summary>
    public string? Reference { get; set; }

    public string? Counterparty { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }

    /// <summary>Shop warehouse for a sale; the platform's origin location for a fiscal event.</summary>
    public string? Location { get; set; }

    /// <summary>Where it came from: the sale's source system, payment provider, or fiscal origin route.</summary>
    public string? Channel { get; set; }

    public string Status { get; set; } = string.Empty;
    public bool IsFailure { get; set; }
    public string? Detail { get; set; }

    // Sales only: the fiscal state this system holds for the sale.
    public string? FiscalStatus { get; set; }

    // Fiscal events only.
    public int? FiscalDeviceId { get; set; }
    public int? FiscalDayNo { get; set; }
    public int? ReceiptGlobalNo { get; set; }
    public string? ReceiptType { get; set; }

    /// <summary>
    /// For a fiscal receipt or attempt, the event id of the sale or invoice here that it fiscalised, when
    /// one could be matched by invoice number. Null for documents fiscalised from elsewhere (the SAP
    /// bridge, the console). The linked event may predate the reader's window, so a reader that does not
    /// hold it simply has nothing to update.
    /// </summary>
    public string? LinkedEventId { get; set; }
}

public static class LiveTransactionKinds
{
    public const string Sale = "Sale";
    public const string Invoice = "Invoice";
    public const string IncomingPayment = "IncomingPayment";
    public const string MobilePayment = "MobilePayment";
    public const string FiscalReceipt = "FiscalReceipt";
    public const string FiscalAttempt = "FiscalAttempt";
    public const string FiscalDay = "FiscalDay";
}
