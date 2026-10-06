namespace ShopInventory.DTOs;

public class ConvertSalesOrderToInvoiceResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int SalesOrderId { get; set; }
    public string? SalesOrderNumber { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string? ReservationId { get; set; }
    public int? QueueId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int EstimatedProcessingSeconds { get; set; }
    public string? StatusUrl { get; set; }
    public List<string> Errors { get; set; } = new();

    // What a van conversion signed before it answered. Null on every other conversion, which is queued
    // unsigned and fiscalised by the queue later.

    /// <summary>The platform's number for the invoice (<c>INV10427</c>), which the slip is headed with.</summary>
    public string? SaleNumber { get; set; }

    /// <summary>Signed, and SAP has still to take the invoice — the ordinary van answer.</summary>
    public bool WasQueued { get; set; }

    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }
    public string? VerificationCode { get; set; }
    public string? QrCode { get; set; }
    public string? FiscalDay { get; set; }
    public string? ReceiptGlobalNo { get; set; }
    public string? DeviceSerial { get; set; }
}
