using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNotes.Commands.ApproveCreditNote;

public sealed record ApproveCreditNoteCommand(int Id) : IRequest<ErrorOr<CreditNoteDto>>;