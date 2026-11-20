using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.RouteCustomers.Queries.ExportRouteCustomerActivity;

/// <summary>
/// The Route Customers page's activity workbook.
/// </summary>
/// <param name="WindowDays">The activity window, in days, ending today.</param>
/// <param name="OnlyCustomerIds">
/// The customers the page is showing, when the export is of the current view; null for every route customer.
/// </param>
/// <param name="IncludeRemoved">
/// For an export of every customer: whether customers taken off the vans, and the sales of deleted ones, are
/// included. Ignored for the current view, whose status filter has already decided.
/// </param>
/// <param name="Sheets">The sheets wanted.</param>
/// <param name="Customers">The page's customer records, for the contact fields the sales summary does not carry.</param>
/// <param name="RouteLabels">"VAN008 - Van Sales West 2" by account code.</param>
/// <param name="ScopeDescription">What was exported, in words, for the summary sheet.</param>
public sealed record ExportRouteCustomerActivityQuery(
    int WindowDays,
    IReadOnlySet<int>? OnlyCustomerIds,
    bool IncludeRemoved,
    RouteCustomerActivitySheets Sheets,
    IReadOnlyCollection<RouteCustomerModel> Customers,
    IReadOnlyDictionary<string, string> RouteLabels,
    string ScopeDescription) : IRequest<ErrorOr<byte[]>>;
