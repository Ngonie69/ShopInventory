using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.RouteCustomers.Queries.ReadRouteScheduleFile;

public sealed class ReadRouteScheduleFileHandler
    : IRequestHandler<ReadRouteScheduleFileQuery, ErrorOr<RouteScheduleWorkbookModel>>
{
    public Task<ErrorOr<RouteScheduleWorkbookModel>> Handle(
        ReadRouteScheduleFileQuery request,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.Content, writable: false);
        return Task.FromResult(RouteScheduleWorkbook.Read(stream));
    }
}
