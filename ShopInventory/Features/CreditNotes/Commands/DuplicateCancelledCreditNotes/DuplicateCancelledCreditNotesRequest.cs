namespace ShopInventory.Features.CreditNotes.Commands.DuplicateCancelledCreditNotes;

public sealed record DuplicateCancelledCreditNotesRequest(
    IReadOnlyList<int> CreditNoteDocEntries,
    string? Reason);