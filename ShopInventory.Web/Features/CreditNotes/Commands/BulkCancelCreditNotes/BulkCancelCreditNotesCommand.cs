using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.CreditNotes.Commands.BulkCancelCreditNotes;

public sealed record BulkCancelCreditNotesCommand(
    IReadOnlyList<int> CreditNoteDocEntries,
    string? Reason) : IRequest<ErrorOr<BulkCancelCreditNotesResult>>;