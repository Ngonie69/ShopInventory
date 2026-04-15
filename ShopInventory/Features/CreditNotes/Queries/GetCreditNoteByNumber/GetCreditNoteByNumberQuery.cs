using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNotes.Queries.GetCreditNoteByNumber;

public sealed record GetCreditNoteByNumberQuery(string CreditNoteNumber) : IRequest<ErrorOr<CreditNoteDto>>;
