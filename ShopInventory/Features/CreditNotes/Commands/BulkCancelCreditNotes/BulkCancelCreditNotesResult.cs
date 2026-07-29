namespace ShopInventory.Features.CreditNotes.Commands.BulkCancelCreditNotes;

public sealed record BulkCancelCreditNotesResult(
    int RequestedCount,
    int SuccessCount,
    int FailedCount,
    IReadOnlyList<BulkCancelCreditNoteResultItem> Results);