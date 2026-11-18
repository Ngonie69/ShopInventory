using ShopInventory.Common.Fiscalization;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.FiscalReceipts.Commands.PickUpFailedFiscalReceipts;

/// <summary>A kind of SAP document the pick-up asks the fiscal device about.</summary>
/// <param name="DocumentType">Its <c>DocumentType</c> in the fiscal transaction log.</param>
/// <param name="ReceiptType">The receipt the device files it as.</param>
/// <param name="Label">How the log names it.</param>
/// <param name="OwingStatuses">
/// The statuses its writers record a failed attempt under. Not the same for both: the invoice routes record
/// <c>Failed</c>, while the credit-note create route records the read-back that follows its attempt, which says
/// <c>Not Fiscalised</c>.
/// </param>
public sealed record FiscalReceiptPickupDocument(
    string DocumentType,
    ReceiptType ReceiptType,
    string Label,
    IReadOnlyList<string> OwingStatuses)
{
    public static readonly FiscalReceiptPickupDocument Invoice = new(
        "Invoice",
        ReceiptType.FiscalInvoice,
        "invoice",
        [FiscalTransactionStatus.Failed]);

    public static readonly FiscalReceiptPickupDocument CreditNote = new(
        "CreditNote",
        ReceiptType.CreditNote,
        "credit note",
        [FiscalTransactionStatus.Failed, NotFiscalisedStatus]);

    private const string NotFiscalisedStatus = "Not Fiscalised";
}
