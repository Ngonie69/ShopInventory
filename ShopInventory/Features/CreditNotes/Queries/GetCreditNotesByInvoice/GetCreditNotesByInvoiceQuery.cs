using ErrorOr;
using MediatR;
using ShopInventory.Controllers;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CreditNotes.Queries.GetCreditNotesByInvoice;

public sealed record GetCreditNotesByInvoiceQuery(int InvoiceId) : IRequest<ErrorOr<CreditNotesByInvoiceResponse>>;
