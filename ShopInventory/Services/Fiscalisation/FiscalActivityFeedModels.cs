namespace ShopInventory.Services.Fiscalisation;

// Wire contract for the platform's GET /api/activity/feed, copied field-for-field from its
// GetActivityFeed response the same way FiscalisationApiModels.cs carries the receipt contract. Nothing
// checks the two stay in step, so a change on the platform side is a human check here too.
//
// Unlike the receipt contract, every enum-like field on this one is sent as its name, so these are
// strings rather than enums: a new outcome the platform adds shows up as text instead of failing the
// whole poll.

public sealed class FiscalActivityFeedApiResponse
{
    public DateTimeOffset ServerTime { get; set; }

    /// <summary>
    /// The platform cut the page at the limit. Ask again from the last event's time for the rest.
    /// </summary>
    public bool HasMore { get; set; }

    public List<FiscalActivityEventApiDto> Events { get; set; } = [];
}

public sealed class FiscalActivityEventApiDto
{
    /// <summary>
    /// Stable for an unchanged row, new when the row changes — a retried attempt gets a new id — so a
    /// reader that re-polls an overlapping window can de-duplicate on it.
    /// </summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>Receipt, Attempt or FiscalDay.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Carries the platform's +02:00 offset, so it converts to UTC without guessing a zone.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public int DeviceId { get; set; }
    public int? FiscalDayNo { get; set; }
    public string? ReceiptType { get; set; }
    public string? InvoiceNo { get; set; }
    public string? Currency { get; set; }
    public decimal? Total { get; set; }
    public int? ReceiptGlobalNo { get; set; }
    public int? ReceiptCounter { get; set; }

    /// <summary>
    /// Receipt: Fiscalised or CapturedOffline. Attempt: the attempt outcome's name. FiscalDay: the
    /// outcome of the open or close (Succeeded, Blocked, Failed, Indeterminate).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>FiscalDay only: what was attempted, as the platform recorded it.</summary>
    public string? Action { get; set; }

    public bool IsFailure { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
    public string? OriginRoute { get; set; }
    public string? OriginClient { get; set; }
    public string? OriginChannel { get; set; }
    public string? OriginLocation { get; set; }
}
