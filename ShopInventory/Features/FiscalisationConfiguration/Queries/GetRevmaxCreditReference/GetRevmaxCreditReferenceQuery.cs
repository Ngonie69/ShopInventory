using ErrorOr;
using MediatR;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetRevmaxCreditReference;

/// <summary>
/// What a credit note filed on the fiscalisation platform must cite to credit an invoice REVMax filed.
/// </summary>
/// <remarks>
/// For credit notes raised against invoices from before the platform took over — the 15% VAT invoices
/// credited after the rate moved to 15.5% are the case that needed it. FDMS matches the note on the
/// original's device, global number and fiscal day, and the platform checks the note against the
/// original's currency, total and taxes, so all of them are read off the device here rather than off SAP.
/// </remarks>
public sealed record GetRevmaxCreditReferenceQuery(int InvoiceDocNum)
    : IRequest<ErrorOr<RevmaxCreditReferenceResult>>;

/// <summary>
/// The original receipt as REVMax holds it.
/// </summary>
/// <remarks>
/// <see cref="FiscalDayNo"/> is null when no receipt of ours proves the day, with the reason in
/// <see cref="FiscalDayUnresolvedReason"/>. Never guessed: REVMax's <c>FiscalDay</c> envelope is the device's
/// day now, not the receipt's, and a wrong day on a credit note is a misstatement ZIMRA flags and nobody can
/// amend. The caller has to find the day elsewhere (the ZIMRA taxpayer portal) or not credit the invoice.
/// <see cref="ReceiptDate"/> is the device's own wall-clock time, as it printed on the receipt.
/// </remarks>
public sealed record RevmaxCreditReferenceResult(
    int InvoiceDocNum,
    int DeviceId,
    int ReceiptGlobalNo,
    int ReceiptCounter,
    int? FiscalDayNo,
    string? FiscalDayUnresolvedReason,
    DateTime ReceiptDate,
    string ReceiptCurrency,
    decimal ReceiptTotal,
    List<RevmaxCreditReferenceTax> Taxes,
    string? VerificationCode,
    string? QrCode);

public sealed record RevmaxCreditReferenceTax(
    int TaxId,
    decimal TaxPercent,
    string? TaxCode,
    decimal SalesAmountWithTax,
    decimal TaxAmount);
