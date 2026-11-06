using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CreditNotes;

/// <summary>
/// Finds the fiscal receipt of each credit memo for a page or document a customer reads.
/// </summary>
/// <remarks>
/// The fiscal log is keyed by DocNum, and SAP reissued numbers after its September 2026 update, so a
/// DocNum alone can name another customer's memo. <see cref="FiscalDocumentStatusProjector"/> answers
/// staff screens on the number alone; a customer is shown a receipt only when the log row also says it
/// belongs to their account — by its card code, or, on a row that recorded none, by its total.
/// A row that cannot be tied to the memo is treated as no receipt at all: a missing QR is a gap the
/// customer can ask about, another customer's QR is a disclosure.
/// </remarks>
internal static class CreditNoteReceiptLookup
{
    private const string CreditNoteDocumentType = "CreditNote";
    private const decimal TotalTolerance = 0.01m;

    /// <summary>The receipt of each memo that has one, keyed by the memo's DocEntry.</summary>
    public static async Task<IReadOnlyDictionary<int, CreditNoteReceipt>> FindAsync(
        ApplicationDbContext dbContext,
        IReadOnlyCollection<SAPCreditNote> creditNotes,
        CancellationToken cancellationToken)
    {
        var docNums = creditNotes
            .Select(creditNote => creditNote.DocNum)
            .Where(docNum => docNum > 0)
            .Distinct()
            .ToList();

        if (docNums.Count == 0)
        {
            return new Dictionary<int, CreditNoteReceipt>();
        }

        var rows = await dbContext.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction => transaction.DocumentType == CreditNoteDocumentType && docNums.Contains(transaction.DocNum))
            .Select(transaction => new DesktopFiscalTransactionEntity
            {
                DocNum = transaction.DocNum,
                Status = transaction.Status,
                CardCode = transaction.CardCode,
                DocTotal = transaction.DocTotal,
                ReceiptGlobalNo = transaction.ReceiptGlobalNo,
                QRCode = transaction.QRCode,
                VerificationCode = transaction.VerificationCode,
                FiscalDay = transaction.FiscalDay,
                DeviceId = transaction.DeviceId,
                TimestampUtc = transaction.TimestampUtc,
                LastSyncedAtUtc = transaction.LastSyncedAtUtc
            })
            .ToListAsync(cancellationToken);

        var receipts = new Dictionary<int, CreditNoteReceipt>();

        foreach (var creditNote in creditNotes)
        {
            // Evidence first, then the freshest — the projector's preference, kept so the portal and
            // the staff list pick the same row whenever both may see it.
            var row = rows
                .Where(transaction => transaction.DocNum == creditNote.DocNum && BelongsTo(transaction, creditNote))
                .OrderByDescending(FiscalDocumentStatusProjector.HasFiscalEvidencePredicate)
                .ThenByDescending(transaction => transaction.LastSyncedAtUtc)
                .ThenByDescending(transaction => transaction.TimestampUtc)
                .FirstOrDefault();

            if (row is not null && FiscalDocumentStatusProjector.HasFiscalEvidencePredicate(row))
            {
                receipts[creditNote.DocEntry] = new CreditNoteReceipt(
                    row.ReceiptGlobalNo,
                    Blank(row.QRCode),
                    Blank(row.VerificationCode),
                    Blank(row.FiscalDay),
                    Blank(row.DeviceId));
            }
        }

        return receipts;
    }

    /// <summary>Whether a fiscal log row is this memo's, not merely a memo with the same number.</summary>
    internal static bool BelongsTo(DesktopFiscalTransactionEntity transaction, SAPCreditNote creditNote)
    {
        if (!string.IsNullOrWhiteSpace(transaction.CardCode))
        {
            return string.Equals(
                transaction.CardCode.Trim(),
                creditNote.CardCode?.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        // No account on the row. Its total is the only other thing it says about the document, in
        // either of SAP's currencies and in either sign — a credit is logged negative by some writers.
        var logged = Math.Abs(transaction.DocTotal);
        return Math.Abs(logged - Math.Abs(creditNote.DocTotal)) <= TotalTolerance
            || (creditNote.DocTotalFc != 0m && Math.Abs(logged - Math.Abs(creditNote.DocTotalFc)) <= TotalTolerance);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
