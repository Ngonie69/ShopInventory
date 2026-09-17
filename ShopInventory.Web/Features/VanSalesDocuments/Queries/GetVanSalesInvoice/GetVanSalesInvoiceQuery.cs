using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.VanSalesDocuments.Queries.GetVanSalesInvoice;

/// <summary>One van invoice, with its lines and receipt, by van order.</summary>
public sealed record GetVanSalesInvoiceQuery(string Reference)
    : IRequest<ErrorOr<VanSalesInvoiceDetailModel>>;
