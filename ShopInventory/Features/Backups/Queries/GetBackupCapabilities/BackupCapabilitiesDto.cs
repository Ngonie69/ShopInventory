namespace ShopInventory.Features.Backups.Queries.GetBackupCapabilities;

public sealed record BackupCapabilitiesDto(
    bool CloudUploadAvailable,
    string CloudProvider,
    string CloudUploadMessage);