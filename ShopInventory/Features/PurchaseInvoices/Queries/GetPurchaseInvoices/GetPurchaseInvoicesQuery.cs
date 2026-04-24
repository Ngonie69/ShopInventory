using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.PurchaseInvoices.Queries.GetPurchaseInvoices;

public sealed record GetPurchaseInvoicesQuery(
    int Page = 1,
    int PageSize = 20,
    string? CardCode = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null
) : IRequest<ErrorOr<PurchaseInvoiceListResponseDto>>;