namespace ShopInventory.DTOs;

/// <summary>What is being written off, out of where, and why.</summary>
public sealed class CreateStockWriteOffRequestDto
{
    public string WarehouseCode { get; set; } = string.Empty;

    /// <summary>
    /// One of the reasons the API offers. Written to every SAP line, because SAP keeps the reason on
    /// the line rather than the header.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    public string? Remarks { get; set; }

    /// <summary>
    /// Optional posting date, <c>yyyy-MM-dd</c>. Left unset, today's business date is used.
    /// </summary>
    public string? DocDate { get; set; }

    /// <summary>
    /// Supplied by the caller so a submit retried after a lost reply finds the write-off it already
    /// raised. Also accepted through the <c>Idempotency-Key</c> header.
    /// </summary>
    public string? ClientRequestId { get; set; }

    public List<CreateStockWriteOffLineDto> Lines { get; set; } = [];
}

/// <summary>One thing counted: an item, the batch or serial it came out of, and how much.</summary>
public sealed class CreateStockWriteOffLineDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public string? UoMCode { get; set; }

    /// <summary>
    /// Required for a batch-managed item: SAP refuses the whole document when a batch-managed line
    /// names no batch. Left null for an item SAP does not manage by batch.
    /// </summary>
    public string? BatchNumber { get; set; }

    /// <summary>Required for a serial-managed item, one line per unit.</summary>
    public string? SerialNumber { get; set; }
}

/// <summary>One write-off as a row in a list.</summary>
public sealed class StockWriteOffSummaryDto
{
    public int Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string RaisedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int LineCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public int? SapDocNum { get; set; }
}

/// <summary>A page of write-offs, with how many sit in each status.</summary>
public sealed class StockWriteOffListResponseDto
{
    public List<StockWriteOffSummaryDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }

    /// <summary>Every status with its count, filters aside, so the tabs can show them.</summary>
    public Dictionary<string, int> StatusCounts { get; set; } = [];
}

/// <summary>One write-off with its lines and what happened to it.</summary>
public sealed class StockWriteOffDetailDto
{
    public int Id { get; set; }
    public string ClientRequestId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid RaisedByUserId { get; set; }
    public string RaisedByName { get; set; } = string.Empty;
    public string WarehouseCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public DateTime? LastAttemptedAtUtc { get; set; }
    public string? LastError { get; set; }
    public List<StockWriteOffLineDto> Lines { get; set; } = [];
}

public sealed class StockWriteOffLineDto
{
    public int Id { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public decimal Quantity { get; set; }
    public string? UoMCode { get; set; }
    public string? BatchNumber { get; set; }
    public string? SerialNumber { get; set; }
}

/// <summary>What raising a write-off did, with the record as it now stands.</summary>
public sealed class StockWriteOffResultDto
{
    public string Message { get; set; } = string.Empty;

    /// <summary>True when this was a resend of a write-off the server had already posted.</summary>
    public bool AlreadyPosted { get; set; }

    public StockWriteOffDetailDto WriteOff { get; set; } = new();
}

/// <summary>One reason a write-off may be given.</summary>
public sealed class StockWriteOffReasonDto
{
    public string Value { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>The reasons a write-off may carry, and whether SAP itself will record them.</summary>
public sealed class StockWriteOffReasonsResponseDto
{
    public List<StockWriteOffReasonDto> Reasons { get; set; } = [];

    /// <summary>
    /// False when this company database defines no reason user field on the goods-issue line table,
    /// in which case the reason is kept on the local record and in the document's comments only.
    /// </summary>
    public bool RecordedInSap { get; set; }
}
