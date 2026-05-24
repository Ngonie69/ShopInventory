namespace ShopInventory.Web.Features.CreditNotes.Commands.DuplicateCancelledCreditNotes;

public sealed record DuplicateCancelledCreditNotesExportResult(
    DuplicateCancelledCreditNotesResult OperationResult,
    string FileName,
    string ContentType,
    string Base64Content);