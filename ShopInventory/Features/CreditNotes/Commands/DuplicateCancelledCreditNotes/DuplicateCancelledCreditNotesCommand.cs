using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CreditNotes.Commands.DuplicateCancelledCreditNotes;

public sealed record DuplicateCancelledCreditNotesCommand(
    DuplicateCancelledCreditNotesRequest Request,
    Guid UserId) : IRequest<ErrorOr<DuplicateCancelledCreditNotesResult>>;