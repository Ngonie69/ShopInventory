using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.PurchaseInvoices.Commands.CreatePurchaseInvoice;

public sealed record CreatePurchaseInvoiceCommand(CreatePurchaseInvoiceRequest Request) : IRequest<ErrorOr<PurchaseInvoiceDto>>;