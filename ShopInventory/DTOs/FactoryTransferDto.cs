namespace ShopInventory.DTOs;

/// <summary>A factory transfer as the list shows it, with what the caller may do to it.</summary>
public sealed class FactoryTransferSummaryDto
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string DepartmentCode { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    /// <summary>Release (production to dispatch) or Return (dispatch to production).</summary>
    public string Direction { get; set; } = string.Empty;
    public string SourceWarehouseCode { get; set; } = string.Empty;
    public string HoldingWarehouseCode { get; set; } = string.Empty;
    public string DestinationWarehouseCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string SentByName { get; set; } = string.Empty;
    public DateTime SentAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Decision { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    /// <summary>The out leg's SAP document number; 0 until it is posted.</summary>
    public int OutSapDocNum { get; set; }
    /// <summary>The in leg's SAP document number; 0 until it is posted.</summary>
    public int InSapDocNum { get; set; }
    public int LineCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public bool CanDecide { get; set; }
    public bool CanRecall { get; set; }
    public bool CanRetry { get; set; }
    public bool CanCancel { get; set; }
}

public sealed class FactoryTransferDetailDto
{
    public FactoryTransferSummaryDto Summary { get; set; } = new();
    public string? DecisionReason { get; set; }
    public string? LastError { get; set; }
    public int OutSapDocEntry { get; set; }
    public int InSapDocEntry { get; set; }
    public DateTime? OutPostedAtUtc { get; set; }
    public DateTime? InPostedAtUtc { get; set; }
    public List<FactoryTransferLineDto> Lines { get; set; } = [];
}

public sealed class FactoryTransferLineDto
{
    public int Id { get; set; }
    public int LineNum { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string? UoMCode { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>Null until decided.</summary>
    public decimal? AcceptedQuantity { get; set; }
    public List<FactoryTransferBatchDto> Batches { get; set; } = [];
}

public sealed class FactoryTransferBatchDto
{
    public string BatchNumber { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
}

public sealed class FactoryTransferListResponseDto
{
    public List<FactoryTransferSummaryDto> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    /// <summary>Transfers in transit that the caller may accept or reject, across every page.</summary>
    public int AwaitingMeCount { get; set; }
}

/// <summary>Where the caller may send from and to, and what they may do.</summary>
public sealed class FactoryTransferOptionsDto
{
    public string HoldingWarehouseCode { get; set; } = string.Empty;
    public bool CanSend { get; set; }
    public bool CanAccept { get; set; }
    public bool CanAcceptAny { get; set; }
    public List<string> AssignedWarehouses { get; set; } = [];
    public List<FactoryTransferRouteDto> Routes { get; set; } = [];
}

/// <summary>One warehouse the caller may send from, and where stock sent from it may go.</summary>
public sealed class FactoryTransferRouteDto
{
    public string SourceWarehouseCode { get; set; } = string.Empty;
    public string DepartmentCode { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public List<string> DestinationWarehouseCodes { get; set; } = [];
}

public sealed class SendFactoryTransferRequestDto
{
    /// <summary>A GUID the page keeps per form, so a retried submit finds the transfer it already made.</summary>
    public string ClientRequestId { get; set; } = string.Empty;
    public string SourceWarehouseCode { get; set; } = string.Empty;
    public string DestinationWarehouseCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public List<SendFactoryTransferLineDto> Lines { get; set; } = [];
}

public sealed class SendFactoryTransferLineDto
{
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemDescription { get; set; }
    public string? UoMCode { get; set; }
    public decimal Quantity { get; set; }
}

public sealed class DecideFactoryTransferRequestDto
{
    /// <summary>Accept or Reject.</summary>
    public string Decision { get; set; } = string.Empty;
    /// <summary>Required to reject, or to accept less than was sent.</summary>
    public string? Reason { get; set; }
    /// <summary>The quantity accepted on every line. Ignored on a reject.</summary>
    public List<DecideFactoryTransferLineDto> Lines { get; set; } = [];
}

public sealed class DecideFactoryTransferLineDto
{
    public int LineId { get; set; }
    public decimal AcceptedQuantity { get; set; }
}

public sealed class RecallFactoryTransferRequestDto
{
    public string Reason { get; set; } = string.Empty;
}

public sealed class FactoryTransferActionResultDto
{
    public string Message { get; set; } = string.Empty;
    public FactoryTransferDetailDto Transfer { get; set; } = new();
}
