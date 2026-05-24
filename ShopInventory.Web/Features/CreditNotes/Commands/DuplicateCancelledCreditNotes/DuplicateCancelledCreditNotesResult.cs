namespace ShopInventory.Web.Features.CreditNotes.Commands.DuplicateCancelledCreditNotes;

public sealed record DuplicateCancelledCreditNotesResult(
    int RequestedCount,
    int SuccessCount,
    int FailedCount,
    IReadOnlyList<DuplicateCancelledCreditNoteResultItem> Results);