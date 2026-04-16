using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Backups.Commands.CreateBackup;

public sealed class CreateBackupHandler(
    IBackupService backupService,
    IAuditService auditService,
    ILogger<CreateBackupHandler> logger
) : IRequestHandler<CreateBackupCommand, ErrorOr<BackupDto>>
{
    public async Task<ErrorOr<BackupDto>> Handle(
        CreateBackupCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var backup = await backupService.CreateBackupAsync(command.Request, command.UserId, cancellationToken);
            try { await auditService.LogAsync(AuditActions.CreateBackup, "Backup", backup.Id.ToString(), $"Backup '{backup.FileName}' created", true); } catch { }
            return backup;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating backup");
            return Errors.Backup.CreationFailed(ex.Message);
        }
    }
}
