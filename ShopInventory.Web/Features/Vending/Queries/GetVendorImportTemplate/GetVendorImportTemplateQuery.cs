using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Vending.Queries.GetVendorImportTemplate;

/// <summary>A blank vendor upload template, listing today's depots and their next free codes.</summary>
public sealed record GetVendorImportTemplateQuery : IRequest<ErrorOr<GetVendorImportTemplateResult>>;
