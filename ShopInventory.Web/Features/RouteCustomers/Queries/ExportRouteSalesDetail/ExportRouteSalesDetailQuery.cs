using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.RouteCustomers.Queries.ExportRouteSalesDetail;

/// <summary>
/// The Route Sales Report's detail workbook, for the report already on screen.
/// </summary>
/// <param name="Summary">The summary the page is showing; its own dates are the ones the items are read for.</param>
/// <param name="ProductMix">The product mix the page holds, if it has loaded; read again when null.</param>
/// <param name="AssignedBusinessPartnerCode">The route filter, or null for every route.</param>
/// <param name="RouteLabels">"VAN008 - Van Sales West 2" by account code, for the route columns.</param>
/// <param name="Currency">The currency the overview is ranking in; the workbook's charts use it too.</param>
public sealed record ExportRouteSalesDetailQuery(
    RouteCustomerSalesSummaryModel Summary,
    RouteCustomerProductMixModel? ProductMix,
    string? AssignedBusinessPartnerCode,
    IReadOnlyDictionary<string, string> RouteLabels,
    string? Currency) : IRequest<ErrorOr<byte[]>>;
