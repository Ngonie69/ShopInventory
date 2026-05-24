using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.CreditNotes.Commands.DeleteCreditNote;

public sealed record DeleteCreditNoteCommand(int Id) : IRequest<ErrorOr<Success>>;