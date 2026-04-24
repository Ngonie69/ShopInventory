using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseInvoices.Queries.GetPurchaseInvoiceByDocEntry;

public sealed record GetPurchaseInvoiceByDocEntryQuery(int DocEntry) : IRequest<ErrorOr<PurchaseInvoiceDto>>;