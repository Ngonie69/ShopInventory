using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Backups.Support;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Backups.Commands.CreateBackup;

public sealed class CreateBackupHandler(
    IBackupService backupService,
    IBackupCloudStorage backupCloudStorage,
    ApplicationDbContext dbContext,
    IAuditService auditService,
    ILogger<CreateBackupHandler> logger
) : IRequestHandler<CreateBackupCommand, ErrorOr<BackupDto>>
{
    public async Task<ErrorOr<BackupDto>> Handle(
        CreateBackupCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Request.UploadToCloud && !backupCloudStorage.IsConfigured)
        {
            return Errors.Backup.ValidationFailed(
                backupCloudStorage.UnavailableReason ?? "Cloud backup upload is not configured.");
        }

        try
        {
            var backup = await backupService.CreateBackupAsync(
                new CreateBackupRequest
                {
                    BackupType = command.Request.BackupType,
                    Description = command.Request.Description,
                    UploadToCloud = false
                },
                command.UserId,
                cancellationToken);

            if (command.Request.UploadToCloud)
            {
                var backupEntity = await dbContext.Backups.FirstOrDefaultAsync(b => b.Id == backup.Id, cancellationToken);
                if (backupEntity is null)
                {
                    return Errors.Backup.NotFound(backup.Id);
                }

                try
                {
                    var cloudUrl = await backupCloudStorage.UploadAsync(
                        backupEntity.FilePath,
                        backupEntity.FileName,
                        cancellationToken);

                    backupEntity.IsOffsite = true;
                    backupEntity.CloudUrl = cloudUrl;
                    backupEntity.ErrorMessage = null;

                    await dbContext.SaveChangesAsync(cancellationToken);

                    backup.IsOffsite = true;
                    backup.CloudUrl = cloudUrl;
                }
                catch (Exception ex)
                {
                    backupEntity.Status = "Failed";
                    backupEntity.IsOffsite = false;
                    backupEntity.CloudUrl = null;
                    backupEntity.ErrorMessage = $"Cloud upload failed: {ex.Message}";
                    backupEntity.CompletedAt = DateTime.UtcNow;

                    await dbContext.SaveChangesAsync(cancellationToken);

                    logger.LogError(
                        ex,
                        "Error uploading backup {BackupId} to {ProviderName}",
                        backup.Id,
                        backupCloudStorage.ProviderName);

                    return Errors.Backup.CloudUploadFailed(
                        $"Backup was created locally, but upload to {backupCloudStorage.ProviderName} failed: {ex.Message}");
                }
            }

            var auditDescription = command.Request.UploadToCloud
                ? $"Backup '{backup.FileName}' created and uploaded to {backupCloudStorage.ProviderName}"
                : $"Backup '{backup.FileName}' created";

            try { await auditService.LogAsync(AuditActions.CreateBackup, "Backup", backup.Id.ToString(), auditDescription, true); } catch { }
            return backup;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating backup");
            return Errors.Backup.CreationFailed(ex.Message);
        }
    }
}
