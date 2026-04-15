using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNotes.Commands.CreateCreditNote;

public sealed record CreateCreditNoteCommand(
    CreateCreditNoteRequest Request,
    Guid UserId
) : IRequest<ErrorOr<CreditNoteDto>>;
