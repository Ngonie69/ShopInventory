using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.CreditNotes.Commands.DuplicateCancelledCreditNotes;

public sealed record DuplicateCancelledCreditNotesCommand(
    IReadOnlyList<int> CreditNoteDocEntries,
    string? Reason) : IRequest<ErrorOr<DuplicateCancelledCreditNotesExportResult>>;