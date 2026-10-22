using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.RouteCustomers.Queries.ReadRouteScheduleFile;

/// <summary>The outlets under each weekday of an uploaded route workbook. Matching them to shops is the API's job.</summary>
public sealed record ReadRouteScheduleFileQuery(byte[] Content) : IRequest<ErrorOr<RouteScheduleWorkbookModel>>;
