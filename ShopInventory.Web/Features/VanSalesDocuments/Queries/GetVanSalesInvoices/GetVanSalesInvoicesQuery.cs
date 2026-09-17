using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.VanSalesDocuments.Queries.GetVanSalesInvoices;

/// <summary>A page of the invoices the van sales app created.</summary>
public sealed record GetVanSalesInvoicesQuery(VanSalesDocumentFilter Filter)
    : IRequest<ErrorOr<VanSalesInvoicesResponse>>;
