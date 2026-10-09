namespace ShopInventory.Web.Models;

/// <summary>
/// Mirror of ShopInventory.DTOs.LiveTransactionFeedDto. See that file for the paging rules.
/// </summary>
public sealed class LiveTransactionFeedModel
{
    public DateTime ServerTimeUtc { get; set; }
    public bool HasMore { get; set; }
    public bool FiscalFeedAvailable { get; set; }
    public string? FiscalFeedMessage { get; set; }
    public List<LiveTransactionEventModel> Events { get; set; } = [];
}

/// <summary>Mirror of ShopInventory.DTOs.LiveTransactionEventDto.</summary>
public sealed class LiveTransactionEventModel
{
    public string EventId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public string? Reference { get; set; }
    public string? Counterparty { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Location { get; set; }
    public string? Channel { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsFailure { get; set; }
    public string? Detail { get; set; }
    public string? FiscalStatus { get; set; }
    public int? FiscalDeviceId { get; set; }
    public int? FiscalDayNo { get; set; }
    public int? ReceiptGlobalNo { get; set; }
    public string? ReceiptType { get; set; }
    public string? LinkedEventId { get; set; }
}

/// <summary>Mirror of ShopInventory.DTOs.LiveTransactionKinds.</summary>
public static class LiveTransactionKinds
{
    public const string Sale = "Sale";
    public const string Invoice = "Invoice";
    public const string IncomingPayment = "IncomingPayment";
    public const string MobilePayment = "MobilePayment";
    public const string FiscalReceipt = "FiscalReceipt";
    public const string FiscalAttempt = "FiscalAttempt";
    public const string FiscalDay = "FiscalDay";

    public static bool IsFiscal(string kind) =>
        kind is FiscalReceipt or FiscalAttempt or FiscalDay;
}
