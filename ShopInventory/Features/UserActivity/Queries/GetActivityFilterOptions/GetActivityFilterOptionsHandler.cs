using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.UserActivity.Queries.GetActivityFilterOptions;

public sealed class GetActivityFilterOptionsHandler(
    ApplicationDbContext context,
    ILogger<GetActivityFilterOptionsHandler> logger
) : IRequestHandler<GetActivityFilterOptionsQuery, ErrorOr<UserActivityFilterOptionsDto>>
{
    public async Task<ErrorOr<UserActivityFilterOptionsDto>> Handle(
        GetActivityFilterOptionsQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var auditLogs = context.AuditLogs.AsNoTracking().AsQueryable();

            if (query.StartDate.HasValue)
            {
                auditLogs = auditLogs.Where(log => log.Timestamp >= query.StartDate.Value);
            }

            if (query.EndDate.HasValue)
            {
                auditLogs = auditLogs.Where(log => log.Timestamp <= query.EndDate.Value);
            }

            var usersTask = auditLogs
                .Where(log => log.Username != null && log.Username != string.Empty)
                .Select(log => log.Username)
                .Distinct()
                .OrderBy(username => username)
                .ToListAsync(cancellationToken);

            var actionsTask = auditLogs
                .Where(log => log.Action != null && log.Action != string.Empty)
                .Select(log => log.Action)
                .Distinct()
                .OrderBy(action => action)
                .ToListAsync(cancellationToken);

            await Task.WhenAll(usersTask, actionsTask);

            return new UserActivityFilterOptionsDto
            {
                Users = usersTask.Result,
                Actions = actionsTask.Result
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching activity filter options");
            return Errors.UserActivity.FetchFailed(ex.Message);
        }
    }
}