using System.Globalization;
using ErrorOr;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNoteApprovals;

/// <summary>
/// Whether a held credit memo draft has already been credited some other way: a credit note for the
/// same customer, the same Customer Ref. No. and the same total, keyed straight into SAP while the
/// draft waited. SAP still holds the draft Pending, so approving and adding it credits the customer a
/// second time.
/// </summary>
/// <remarks>
/// Not a rare case. Read from production on 2026-11-11, 53 of the 120 pending credit memo requests had
/// such a credit note — some keyed again by another user the same afternoon, others days or months
/// later — and nothing in SAP links the two.
///
/// The match is deliberately narrow because it blocks an approval: all three of customer, reference and
/// total must agree, and the credit note must have been entered no earlier than the draft's day. A
/// credit note agreeing on customer and total alone is not enough — crate returns of a round sum to the
/// same customer are routine — and a draft with no reference is never flagged.
/// </remarks>
internal static class CreditNoteAlreadyCredited
{
    /// <summary>Only a request somebody can still act on — pending a decision, or approved and waiting to be added.</summary>
    public static bool AppliesTo(SAPApprovalRequest request) =>
        string.Equals(request.Status, SapApprovalRequestStatuses.Pending, StringComparison.OrdinalIgnoreCase)
        || string.Equals(request.Status, SapApprovalRequestStatuses.Approved, StringComparison.OrdinalIgnoreCase);

    /// <summary>What to ask SAP for to check this draft, or null when the draft cannot be checked or need not be.</summary>
    public static SapCreditNoteTotalKey? KeyOf(SAPCreditNote? draft)
    {
        if (draft is null
            || !CreditNoteApprovalProjection.IsOpen(draft)
            || string.IsNullOrWhiteSpace(draft.CardCode)
            || string.IsNullOrWhiteSpace(draft.NumAtCard)
            || draft.DocTotal <= 0)
        {
            return null;
        }

        // CreationDate is the day it was keyed; DocDate is typed and can be back-dated, so it is only
        // the fallback for a read that did not carry the other.
        var entered = CreditNoteApprovalProjection.ParseSapDate(draft.CreationDate)
                      ?? CreditNoteApprovalProjection.ParseSapDate(draft.DocDate);

        return entered is DateTime day
            ? new SapCreditNoteTotalKey(draft.CardCode.Trim(), draft.DocTotal, day.Date)
            : null;
    }

    /// <summary>The first credit note among <paramref name="creditNotes"/> that already credits this draft, or null.</summary>
    public static SAPCreditNote? Match(SAPCreditNote draft, IEnumerable<SAPCreditNote> creditNotes)
    {
        if (KeyOf(draft) is not SapCreditNoteTotalKey key)
        {
            return null;
        }

        var reference = draft.NumAtCard!.Trim();

        return creditNotes
            .Where(note => string.Equals(note.CardCode?.Trim(), key.CardCode, StringComparison.OrdinalIgnoreCase)
                           && note.DocTotal == key.DocTotal
                           && string.Equals(note.NumAtCard?.Trim(), reference, StringComparison.OrdinalIgnoreCase)
                           && Credits(note)
                           && CreditNoteApprovalProjection.ParseSapDate(note.CreationDate) is DateTime entered
                           && entered.Date >= key.EnteredOnOrAfter.Date)
            .OrderBy(note => note.DocEntry)
            .FirstOrDefault();
    }

    /// <summary>
    /// Reads, in one batched call, the credit notes that could match these drafts and pairs each draft
    /// with the one that already credits it. Drafts that cannot be checked cost nothing.
    /// </summary>
    /// <returns>The matching credit note, keyed by the draft's DocEntry; drafts with no match are absent.</returns>
    public static async Task<Dictionary<int, SAPCreditNote>> FindAsync(
        ISAPServiceLayerClient sap,
        IEnumerable<SAPCreditNote> drafts,
        CancellationToken cancellationToken)
    {
        var checkable = drafts
            .Where(draft => KeyOf(draft) is not null)
            .DistinctBy(draft => draft.DocEntry)
            .ToList();
        if (checkable.Count == 0)
        {
            return [];
        }

        var creditNotes = await sap.GetCreditNotesByCustomerTotalsAsync(
            checkable.Select(draft => KeyOf(draft)!).ToList(), cancellationToken);

        var matched = new Dictionary<int, SAPCreditNote>();
        foreach (var draft in checkable)
        {
            if (Match(draft, creditNotes) is SAPCreditNote creditNote)
            {
                matched[draft.DocEntry] = creditNote;
            }
        }

        return matched;
    }

    /// <summary>
    /// The guard the approve and add handlers share: a refusal naming the credit note when the draft is
    /// already credited, success when it is not, and a refusal when SAP cannot say — guessing wrong
    /// credits the customer twice, so an unanswered check never lets the write through.
    /// </summary>
    /// <param name="sap">The Service Layer client.</param>
    /// <param name="lookups">Names who keyed the credit note in the refusal; null leaves the name out.</param>
    /// <param name="draft">The draft about to be approved or added.</param>
    /// <param name="canReject">Whether the refused caller could reject the request instead — an approver can, an adder cannot.</param>
    /// <param name="logger">Where the refusal and a failed check are logged.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<ErrorOr<Success>> RefuseIfCreditedAsync(
        ISAPServiceLayerClient sap,
        ISapApprovalLookups? lookups,
        SAPCreditNote draft,
        bool canReject,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (KeyOf(draft) is null)
        {
            return Result.Success;
        }

        SAPCreditNote? creditNote;
        try
        {
            creditNote = (await FindAsync(sap, [draft], cancellationToken)).GetValueOrDefault(draft.DocEntry);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not check whether draft {DraftEntry} was already credited; refusing the write", draft.DocEntry);
            return Errors.CreditNoteApproval.AlreadyCreditedUnchecked;
        }

        if (creditNote is null)
        {
            return Result.Success;
        }

        SAPUser? enteredBy = null;
        if (lookups is not null && creditNote.UserSign is int userSign)
        {
            try
            {
                enteredBy = await lookups.GetUserAsync(userSign, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The refusal stands without the name.
                logger.LogDebug(exception, "Could not name SAP user {UserSign} in an already-credited refusal", userSign);
            }
        }

        logger.LogWarning(
            "Draft {DraftEntry} is already credited by credit note {DocNum} (DocEntry {DocEntry}); refusing the write",
            draft.DocEntry, creditNote.DocNum, creditNote.DocEntry);
        return Errors.CreditNoteApproval.AlreadyCredited(Describe(creditNote, enteredBy, canReject));
    }

    /// <summary>The sentence the page shows and the refusal carries.</summary>
    /// <param name="creditNote">The credit note that already credits the draft.</param>
    /// <param name="enteredBy">Who keyed it; null leaves the name out.</param>
    /// <param name="canReject">
    /// Whether the reader can reject the request here. When they cannot — an approved request, or a
    /// stage the app's SAP user is not on — the draft can only be removed in SAP.
    /// </param>
    public static string Describe(SAPCreditNote creditNote, SAPUser? enteredBy, bool canReject)
    {
        var who = enteredBy?.UserCode is { Length: > 0 } userCode ? $" by {userCode}" : string.Empty;
        var when = CreditNoteApprovalProjection.ParseSapDate(creditNote.CreationDate) is DateTime entered
            ? $" on {entered.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
            : string.Empty;

        return $"Already credited: credit note {creditNote.DocNum} was entered{who}{when} for the same customer, "
               + "reference and total. Approving or adding this draft would credit the customer twice. "
               + (canReject ? "Reject it, or remove the draft in SAP." : "Remove the draft in SAP.");
    }

    /// <summary>Neither a cancelled credit note nor the document that cancelled one credits anybody.</summary>
    private static bool Credits(SAPCreditNote creditNote) =>
        !string.Equals(creditNote.Cancelled, SapYesNo.Yes, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(creditNote.CancelStatus)
            || string.Equals(creditNote.CancelStatus, "csNo", StringComparison.OrdinalIgnoreCase));
}
