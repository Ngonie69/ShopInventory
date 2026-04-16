using ErrorOr;
using MediatR;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax.Queries.GetInvoice;

public sealed record GetInvoiceQuery(string InvoiceNumber) : IRequest<ErrorOr<InvoiceResponse>>;
