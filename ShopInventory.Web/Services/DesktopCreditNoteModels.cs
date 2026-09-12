namespace ShopInventory.Web.Services;

public sealed record DesktopCreditQuantity(int LineNo, decimal Quantity);
public sealed record CreateDesktopCreditRequest(string RequestKey, string Reason, List<DesktopCreditQuantity> Lines);
public sealed record DesktopCreditLine(int LineNo, string Name, decimal Quantity, decimal UnitPrice,
    int TaxId, decimal? TaxPercent, string? TaxCode, string? HsCode);
public sealed record DesktopCreditSource(string OriginalFiscalNumber, string Currency, decimal OriginalTotal,
    int DeviceId, int FiscalDayNo, int ReceiptGlobalNo, long? ReceiptId, List<DesktopCreditLine> Lines,
    List<string>? ExcludedLines = null);
/// <remarks>
/// <c>Status</c> is the fiscal half and <c>SapStatus</c> the back-office half. Both are carried,
/// because the ordinary outcome is that they differ: a credit taken before its sale posts is with
/// ZIMRA at once and owes SAP a document until that evening.
///
/// Both are plain strings rather than enums, so a value this build has not heard of renders as itself
/// instead of failing the whole response. <c>SapStatus</c> has a default for the same reason: an API
/// that predates it must not break the page.
/// </remarks>
public sealed record DesktopCreditNoteResult(Guid Id, string Number, string Status, decimal Amount,
    string Currency, string Reason, string OriginalFiscalNumber, DateTime CreatedAtUtc,
    string? Message, string? QrCode, string? ReceiptGlobalNo, int? SapDocNum,
    string SapStatus = "Deferred", string? SapError = null);

/// <summary>The values <see cref="DesktopCreditNoteResult.SapStatus"/> takes, as the API writes them.</summary>
public static class DesktopCreditSapStatuses
{
    public const string Deferred = "Deferred";
    public const string Posted = "Posted";
    public const string Failed = "Failed";
    public const string NotRequired = "NotRequired";
    public const string ManualInSap = "ManualInSap";
}
public sealed record DesktopCreditForm(DesktopCreditSource Source, List<DesktopCreditNoteResult> CreditNotes,
    Dictionary<int, decimal> ReservedQuantities);
