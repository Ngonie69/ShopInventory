namespace ShopInventory.Web.Features.CreditNotes.Commands.BulkCancelCreditNotes;

public sealed record BulkCancelCreditNoteResultItem(
    int SapDocEntry,
    int? SapDocNum,
    string CreditNoteNumber,
    bool Success,
    string Status,
    string? Message);