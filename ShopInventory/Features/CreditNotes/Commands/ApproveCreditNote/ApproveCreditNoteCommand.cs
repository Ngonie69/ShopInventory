using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNotes.Commands.ApproveCreditNote;

public sealed record ApproveCreditNoteCommand(int Id, Guid UserId) : IRequest<ErrorOr<CreditNoteDto>>;
