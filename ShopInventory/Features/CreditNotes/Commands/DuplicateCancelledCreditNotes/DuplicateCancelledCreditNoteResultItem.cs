namespace ShopInventory.Features.CreditNotes.Commands.DuplicateCancelledCreditNotes;

public sealed record DuplicateCancelledCreditNoteResultItem(
    int OriginalSapDocEntry,
    int? OriginalSapDocNum,
    string OriginalCreditNoteNumber,
    int? NewId,
    int? NewSapDocEntry,
    int? NewSapDocNum,
    string? NewCreditNoteNumber,
    bool Success,
    string Status,
    string? Message);