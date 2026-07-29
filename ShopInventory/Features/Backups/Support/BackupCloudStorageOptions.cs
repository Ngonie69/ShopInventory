namespace ShopInventory.Features.Backups.Support;

public sealed class BackupCloudStorageOptions
{
    public string? Provider { get; init; }

    public string? BucketName { get; init; }

    public string? ObjectPrefix { get; init; }

    public string? ServiceAccountJson { get; init; }
}
