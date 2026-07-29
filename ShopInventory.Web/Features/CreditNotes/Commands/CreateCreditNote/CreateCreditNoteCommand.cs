using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNotes.Commands.CreateCreditNote;

public sealed record CreateCreditNoteCommand(CreateCreditNoteRequest Request) : IRequest<ErrorOr<CreditNoteDto>>;