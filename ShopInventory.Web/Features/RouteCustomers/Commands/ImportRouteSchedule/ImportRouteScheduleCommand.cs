using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.RouteCustomers.Commands.ImportRouteSchedule;

/// <summary>Checks (<c>ValidateOnly</c>) or saves the calling schedule read off the route workbook.</summary>
public sealed record ImportRouteScheduleCommand(RouteScheduleImportRequestModel Request)
    : IRequest<ErrorOr<RouteScheduleImportResultModel>>;
