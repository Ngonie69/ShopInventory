using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SalesOrders.Commands.ConvertToInvoice;

public sealed record ConvertToInvoiceCommand(int Id, Guid UserId) : IRequest<ErrorOr<InvoiceDto>>;
