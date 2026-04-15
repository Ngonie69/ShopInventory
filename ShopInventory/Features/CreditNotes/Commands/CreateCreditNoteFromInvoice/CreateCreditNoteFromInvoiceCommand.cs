using ErrorOr;
using MediatR;
using ShopInventory.Controllers;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNotes.Commands.CreateCreditNoteFromInvoice;

public sealed record CreateCreditNoteFromInvoiceCommand(
    int InvoiceId,
    CreateCreditNoteFromInvoiceApiRequest Request,
    Guid UserId
) : IRequest<ErrorOr<CreditNoteDto>>;
