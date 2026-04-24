using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseInvoices.Commands.CreatePurchaseInvoice;

public sealed record CreatePurchaseInvoiceCommand(CreatePurchaseInvoiceRequest Request) : IRequest<ErrorOr<PurchaseInvoiceDto>>;