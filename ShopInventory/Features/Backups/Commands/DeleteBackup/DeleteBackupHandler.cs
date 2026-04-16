using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Backups.Commands.DeleteBackup;

public sealed class DeleteBackupHandler(
    IBackupService backupService,
    IAuditService auditService,
    ILogger<DeleteBackupHandler> logger
) : IRequestHandler<DeleteBackupCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteBackupCommand command,
        CancellationToken cancellationToken)
    {
        var backup = await backupService.GetBackupByIdAsync(command.Id, cancellationToken);
        if (backup is null)
            return Errors.Backup.NotFound(command.Id);

        try
        {
            var result = await backupService.DeleteBackupAsync(command.Id, cancellationToken);
            if (!result)
                return Errors.Backup.DeleteFailed("Failed to delete backup");

            try { await auditService.LogAsync(AuditActions.DeleteBackup, "Backup", command.Id.ToString(), $"Backup {command.Id} deleted", true); } catch { }
            return Result.Deleted;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting backup {Id}", command.Id);
            return Errors.Backup.DeleteFailed(ex.Message);
        }
    }
}
