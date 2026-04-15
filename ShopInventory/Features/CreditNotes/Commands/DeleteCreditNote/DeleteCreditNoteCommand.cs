using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CreditNotes.Commands.DeleteCreditNote;

public sealed record DeleteCreditNoteCommand(int Id) : IRequest<ErrorOr<Deleted>>;
