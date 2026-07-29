using ErrorOr;
using MediatR;
using ShopInventory.Features.Backups.Support;

namespace ShopInventory.Features.Backups.Queries.GetBackupCapabilities;

public sealed class GetBackupCapabilitiesHandler(
    IBackupCloudStorage backupCloudStorage
) : IRequestHandler<GetBackupCapabilitiesQuery, ErrorOr<BackupCapabilitiesDto>>
{
    public Task<ErrorOr<BackupCapabilitiesDto>> Handle(
        GetBackupCapabilitiesQuery request,
        CancellationToken cancellationToken)
    {
        var message = backupCloudStorage.IsConfigured
            ? $"Backups can be uploaded to {backupCloudStorage.ProviderName}."
            : backupCloudStorage.UnavailableReason ?? "Cloud backup upload is not configured.";

        return Task.FromResult<ErrorOr<BackupCapabilitiesDto>>(
            new BackupCapabilitiesDto(
                backupCloudStorage.IsConfigured,
                backupCloudStorage.ProviderName,
                message));
    }
}