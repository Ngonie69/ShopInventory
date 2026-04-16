using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Backups.Commands.ResetDatabase;

public sealed class ResetDatabaseHandler(
    IBackupService backupService,
    IAuditService auditService,
    ILogger<ResetDatabaseHandler> logger
) : IRequestHandler<ResetDatabaseCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        ResetDatabaseCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogWarning("Database reset requested by {Caller}", command.Caller);
            await backupService.ResetDatabaseAsync(command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.ResetDatabase, "Database", null, $"Database reset by {command.Caller}", true); } catch { }
            return Result.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database reset failed for {Caller}", command.Caller);
            return Errors.Backup.ResetFailed(ex.Message);
        }
    }
}
