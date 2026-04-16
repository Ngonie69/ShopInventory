using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Backups.Queries.GetBackupStats;

public sealed class GetBackupStatsHandler(
    IBackupService backupService
) : IRequestHandler<GetBackupStatsQuery, ErrorOr<BackupStatsDto>>
{
    public async Task<ErrorOr<BackupStatsDto>> Handle(
        GetBackupStatsQuery request,
        CancellationToken cancellationToken)
    {
        var result = await backupService.GetAllBackupsAsync(cancellationToken);

        var totalSize = result.Backups.Sum(b => b.SizeBytes);
        var stats = new BackupStatsDto
        {
            TotalBackups = result.TotalCount,
            SuccessfulBackups = result.Backups.Count(b => b.Status == "Completed"),
            FailedBackups = result.Backups.Count(b => b.Status == "Failed"),
            TotalSizeBytes = totalSize,
            TotalSizeFormatted = FormatSize(totalSize),
            LastBackupAt = result.Backups.FirstOrDefault()?.StartedAt,
            NextScheduledBackup = null
        };

        return stats;
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}
