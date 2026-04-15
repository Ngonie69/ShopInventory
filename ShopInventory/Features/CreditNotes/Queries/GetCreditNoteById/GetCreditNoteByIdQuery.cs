using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNotes.Queries.GetCreditNoteById;

public sealed record GetCreditNoteByIdQuery(int Id) : IRequest<ErrorOr<CreditNoteDto>>;
