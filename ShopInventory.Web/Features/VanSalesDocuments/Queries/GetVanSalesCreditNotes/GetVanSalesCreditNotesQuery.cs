using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.VanSalesDocuments.Queries.GetVanSalesCreditNotes;

/// <summary>A page of the credit notes raised against invoices the van sales app created.</summary>
public sealed record GetVanSalesCreditNotesQuery(VanSalesDocumentFilter Filter)
    : IRequest<ErrorOr<VanSalesCreditNotesResponse>>;
