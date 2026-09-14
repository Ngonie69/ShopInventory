using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.DesktopCreditNotes;

public sealed record DesktopCreditQuantity(int LineNo, decimal Quantity);
/// <remarks>
/// <c>Note</c> is optional and printed on the fiscal credit after the reason.
///
/// <c>PostToSap</c> is which of the dialog's two actions was taken: <c>true</c> is "Fiscalise and post
/// to SAP", offered once the sale has an invoice, and <c>false</c> is "Fiscalise only". Before the sale
/// posts, "Fiscalise only" still owes SAP the memo, raised once it does; after, it means SAP gets
/// nothing at all — a fiscal correction such as reversing a sale ZIMRA was sent twice.
///
/// <c>SaleInSap</c> is what the form showed when the button was pressed. The two meanings of "Fiscalise
/// only" differ by whether SAP ever hears of the credit, so a form opened before the sale posted is
/// refused rather than taken as the second. Null, from a caller that predates the choice, is not checked.
/// </remarks>
public sealed record CreateDesktopCreditRequest(string RequestKey, string Reason, List<DesktopCreditQuantity> Lines,
    string? Note = null, bool? PostToSap = null, bool? SaleInSap = null);
public sealed record DesktopCreditLine(int LineNo, string Name, decimal Quantity, decimal UnitPrice,
    int TaxId, decimal? TaxPercent, string? TaxCode, string? HsCode);
/// <remarks>
/// <c>ExternalCreditedAmount</c> and <c>ExternalCredits</c> are the credits ZIMRA already holds against the
/// receipt that this dialog did not file; see <see cref="DesktopCreditExternalCredits"/>.
/// </remarks>
public sealed record DesktopCreditSource(string OriginalFiscalNumber, string Currency, decimal OriginalTotal,
    int DeviceId, int FiscalDayNo, int ReceiptGlobalNo, long? ReceiptId, List<DesktopCreditLine> Lines,
    decimal ExternalCreditedAmount = 0, BuyerApiRequest? Buyer = null, List<string>? ExcludedLines = null,
    List<string>? ExternalCredits = null);
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
/// <remarks>
/// <c>SaleInSap</c>: the sale has its own SAP invoice, which decides the action the form offers.
/// <c>RemainingAmount</c>: what ZIMRA will still accept against the receipt — its total, less every credit
/// saved here that was not refused and every credit already filed elsewhere. Never below zero.
/// </remarks>
public sealed record DesktopCreditForm(DesktopCreditSource Source, List<DesktopCreditNoteResult> CreditNotes,
    Dictionary<int, decimal> ReservedQuantities, bool SaleInSap = false, int? SaleSapDocNum = null,
    decimal RemainingAmount = 0);

public interface IDesktopCreditFiscalGateway
{
    Task<DesktopCreditSource> ReadOriginalAsync(DesktopSaleEntity sale, CancellationToken ct);
    Task<FiscalizationResult?> FindAsync(DesktopCreditPlan plan, CancellationToken ct);
    Task<string?> PreflightAsync(DesktopCreditPlan plan, CancellationToken ct);
    Task<FiscalizationResult> SubmitAsync(DesktopCreditPlan plan, CancellationToken ct);
}
