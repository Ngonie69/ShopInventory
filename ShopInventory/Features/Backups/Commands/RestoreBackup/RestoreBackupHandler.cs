using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Backups.Commands.RestoreBackup;

public sealed class RestoreBackupHandler(
    IBackupService backupService,
    IAuditService auditService,
    ILogger<RestoreBackupHandler> logger
) : IRequestHandler<RestoreBackupCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RestoreBackupCommand command,
        CancellationToken cancellationToken)
    {
        var backup = await backupService.GetBackupByIdAsync(command.Id, cancellationToken);
        if (backup is null)
            return Errors.Backup.NotFound(command.Id);

        try
        {
            var result = await backupService.RestoreBackupAsync(command.Id, command.UserId, cancellationToken);
            if (!result)
                return Errors.Backup.RestoreFailed("Failed to restore backup");

            try { await auditService.LogAsync(AuditActions.RestoreBackup, "Backup", command.Id.ToString(), $"Backup {command.Id} restored", true); } catch { }
            return Result.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error restoring backup {Id}", command.Id);
            return Errors.Backup.RestoreFailed(ex.Message);
        }
    }
}
