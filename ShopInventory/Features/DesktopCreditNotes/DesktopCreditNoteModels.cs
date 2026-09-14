using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.DesktopCreditNotes;

public sealed record DesktopCreditQuantity(int LineNo, decimal Quantity);
/// <remarks>
/// <c>Note</c> is optional and printed on the fiscal credit after the reason.
///
/// <c>PostToSap</c> is which of the dialog's two actions was taken: <c>false</c> is "Fiscalise only",
/// offered while the sale has no SAP invoice, and <c>true</c> is "Fiscalise and post to SAP", offered
/// once it has one. Checked against the sale, so a form left open while the sale posted cannot take the
/// wrong one. Null, from a caller that predates the choice, is not checked.
/// </remarks>
public sealed record CreateDesktopCreditRequest(string RequestKey, string Reason, List<DesktopCreditQuantity> Lines,
    string? Note = null, bool? PostToSap = null);
public sealed record DesktopCreditLine(int LineNo, string Name, decimal Quantity, decimal UnitPrice,
    int TaxId, decimal? TaxPercent, string? TaxCode, string? HsCode);
public sealed record DesktopCreditSource(string OriginalFiscalNumber, string Currency, decimal OriginalTotal,
    int DeviceId, int FiscalDayNo, int ReceiptGlobalNo, long? ReceiptId, List<DesktopCreditLine> Lines,
    decimal ExternalCreditedAmount = 0, BuyerApiRequest? Buyer = null, List<string>? ExcludedLines = null);
public sealed record DesktopCreditPlan(DesktopCreditSource Source, List<DesktopCreditQuantity> Quantities,
    SubmitReceiptApiRequest Receipt, decimal Amount);
/// <remarks>
/// <paramref name="Status"/> is the fiscal half and <paramref name="SapStatus"/> the back-office half,
/// reported separately because the ordinary outcome is that they differ: a credit taken before its
/// sale posts is with ZIMRA at once and owes SAP a document until that evening. One verdict could not
/// say that, and could not tell it from a fiscal filing that failed.
/// </remarks>
public sealed record DesktopCreditNoteResult(Guid Id, string Number, string Status, decimal Amount,
    string Currency, string Reason, string OriginalFiscalNumber, DateTime CreatedAtUtc,
    string? Message, string? QrCode, string? ReceiptGlobalNo, int? SapDocNum,
    string SapStatus = DesktopCreditSapStatuses.Deferred, string? SapError = null);
/// <remarks><c>SaleInSap</c>: the sale has its own SAP invoice, which decides the action the form offers.</remarks>
public sealed record DesktopCreditForm(DesktopCreditSource Source, List<DesktopCreditNoteResult> CreditNotes,
    Dictionary<int, decimal> ReservedQuantities, bool SaleInSap = false, int? SaleSapDocNum = null);

public interface IDesktopCreditFiscalGateway
{
    Task<DesktopCreditSource> ReadOriginalAsync(DesktopSaleEntity sale, CancellationToken ct);
    Task<FiscalizationResult?> FindAsync(DesktopCreditPlan plan, CancellationToken ct);
    Task<string?> PreflightAsync(DesktopCreditPlan plan, CancellationToken ct);
    Task<FiscalizationResult> SubmitAsync(DesktopCreditPlan plan, CancellationToken ct);
}
