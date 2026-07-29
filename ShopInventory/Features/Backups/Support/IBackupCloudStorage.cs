namespace ShopInventory.Features.Backups.Support;

public interface IBackupCloudStorage
{
    bool IsConfigured { get; }

    string ProviderName { get; }

    string? UnavailableReason { get; }

    Task<string> UploadAsync(string? localFilePath, string fileName, CancellationToken cancellationToken);
}