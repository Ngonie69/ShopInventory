using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CreditNotes.Commands.UpdateCreditNoteStatus;

public sealed record UpdateCreditNoteStatusCommand(
    int Id,
    CreditNoteStatus Status,
    Guid UserId
) : IRequest<ErrorOr<CreditNoteDto>>;
