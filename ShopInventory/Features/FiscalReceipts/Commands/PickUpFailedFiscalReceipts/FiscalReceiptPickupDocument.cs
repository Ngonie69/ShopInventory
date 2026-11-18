using ShopInventory.Common.Fiscalization;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.FiscalReceipts.Commands.PickUpFailedFiscalReceipts;

/// <summary>A kind of SAP document the pick-up asks the fiscal device about.</summary>
/// <param name="DocumentType">Its <c>DocumentType</c> in the fiscal transaction log.</param>
/// <param name="ReceiptType">The receipt the device files it as.</param>
/// <param name="Label">How the log names it.</param>
/// <param name="OwingStatuses">
/// The statuses that say the document had no receipt when the log last heard of it: <c>Failed</c>, from an
/// attempt, and <c>Not Fiscalised</c>, from a read-back. For a credit note the second is the create route's
/// read-back after its attempt. For an invoice it is the status backfill's: the one look-up made when the invoice
/// list first showed an invoice raised in SAP, which nothing asked again. Such an invoice is usually fiscalised
/// later, when it is printed from B1 and the print add-on files it with the platform directly, and until now it
/// read "Not fiscalised" with a Fiscalise button until a person pressed it and was told it already was.
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
        [FiscalTransactionStatus.Failed, NotFiscalisedStatus]);

    public static readonly FiscalReceiptPickupDocument CreditNote = new(
        "CreditNote",
        ReceiptType.CreditNote,
        "credit note",
        [FiscalTransactionStatus.Failed, NotFiscalisedStatus]);

    private const string NotFiscalisedStatus = "Not Fiscalised";
}
