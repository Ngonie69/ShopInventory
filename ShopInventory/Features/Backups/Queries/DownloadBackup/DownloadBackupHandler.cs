using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Backups.Queries.DownloadBackup;

public sealed class DownloadBackupHandler(
    IBackupService backupService
) : IRequestHandler<DownloadBackupQuery, ErrorOr<DownloadBackupResult>>
{
    public async Task<ErrorOr<DownloadBackupResult>> Handle(
        DownloadBackupQuery request,
        CancellationToken cancellationToken)
    {
        var backup = await backupService.GetBackupByIdAsync(request.Id, cancellationToken);
        if (backup is null)
            return Errors.Backup.NotFound(request.Id);

        try
        {
            var stream = await backupService.DownloadBackupAsync(request.Id, cancellationToken);
            if (stream is null)
                return Errors.Backup.DownloadFailed("Backup file not found");

            return new DownloadBackupResult(stream, backup.FileName, "application/octet-stream");
        }
        catch (Exception ex)
        {
            return Errors.Backup.DownloadFailed(ex.Message);
        }
    }
}
