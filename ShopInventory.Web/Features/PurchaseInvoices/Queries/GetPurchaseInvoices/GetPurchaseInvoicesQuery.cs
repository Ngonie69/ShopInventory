using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.PurchaseInvoices.Queries.GetPurchaseInvoices;

public sealed record GetPurchaseInvoicesQuery(
    int Page = 1,
    int PageSize = 20,
    string? CardCode = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null
) : IRequest<ErrorOr<PurchaseInvoiceListResponse>>;