using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Sync.Queries.GetConnectionLogs;

public sealed class GetConnectionLogsHandler(
    ISyncStatusService syncStatusService
) : IRequestHandler<GetConnectionLogsQuery, ErrorOr<List<ConnectionLogDto>>>
{
    public async Task<ErrorOr<List<ConnectionLogDto>>> Handle(
        GetConnectionLogsQuery request,
        CancellationToken cancellationToken)
    {
        var logs = await syncStatusService.GetConnectionLogsAsync(request.Count, cancellationToken);
        return logs;
    }
}
