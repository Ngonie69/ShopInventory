using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.DesktopCreditNotes;

public sealed record DesktopCreditQuantity(int LineNo, decimal Quantity);
public sealed record CreateDesktopCreditRequest(string RequestKey, string Reason, List<DesktopCreditQuantity> Lines);
public sealed record DesktopCreditLine(int LineNo, string Name, decimal Quantity, decimal UnitPrice,
    int TaxId, decimal? TaxPercent, string? TaxCode, string? HsCode);
public sealed record DesktopCreditSource(string OriginalFiscalNumber, string Currency, decimal OriginalTotal,
    int DeviceId, int FiscalDayNo, int ReceiptGlobalNo, long? ReceiptId, List<DesktopCreditLine> Lines,
    decimal ExternalCreditedAmount = 0, BuyerApiRequest? Buyer = null, List<string>? ExcludedLines = null);
public sealed record DesktopCreditPlan(DesktopCreditSource Source, List<DesktopCreditQuantity> Quantities,
    SubmitReceiptApiRequest Receipt, decimal Amount);
public sealed record DesktopCreditNoteResult(Guid Id, string Number, string Status, decimal Amount,
    string Currency, string Reason, string OriginalFiscalNumber, DateTime CreatedAtUtc,
    string? Message, string? QrCode, string? ReceiptGlobalNo, int? SapDocNum);
public sealed record DesktopCreditForm(DesktopCreditSource Source, List<DesktopCreditNoteResult> CreditNotes,
    Dictionary<int, decimal> ReservedQuantities);

public interface IDesktopCreditFiscalGateway
{
    Task<DesktopCreditSource> ReadOriginalAsync(DesktopSaleEntity sale, CancellationToken ct);
    Task<FiscalizationResult?> FindAsync(DesktopCreditPlan plan, CancellationToken ct);
    Task<string?> PreflightAsync(DesktopCreditPlan plan, CancellationToken ct);
    Task<FiscalizationResult> SubmitAsync(DesktopCreditPlan plan, CancellationToken ct);
}
