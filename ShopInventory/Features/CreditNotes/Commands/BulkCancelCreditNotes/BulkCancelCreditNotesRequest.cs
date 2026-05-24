namespace ShopInventory.Features.CreditNotes.Commands.BulkCancelCreditNotes;

public sealed record BulkCancelCreditNotesRequest(
    IReadOnlyList<int> CreditNoteDocEntries,
    string? Reason);