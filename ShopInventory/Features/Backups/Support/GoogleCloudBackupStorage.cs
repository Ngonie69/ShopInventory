using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Backups.Support;

public sealed class GoogleCloudBackupStorage(
    IOptions<BackupCloudStorageOptions> options,
    ILogger<GoogleCloudBackupStorage> logger
) : IBackupCloudStorage
{
    private readonly BackupCloudStorageOptions _options = options.Value;

    public bool IsConfigured => string.IsNullOrEmpty(UnavailableReason);

    public string ProviderName => "Google Cloud Storage";

    public string? UnavailableReason => GetUnavailableReason();

    public async Task<string> UploadAsync(
        string? localFilePath,
        string fileName,
        CancellationToken cancellationToken)
    {
        var unavailableReason = GetUnavailableReason();
        if (!string.IsNullOrEmpty(unavailableReason))
        {
            throw new InvalidOperationException(unavailableReason);
        }

        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
        {
            throw new FileNotFoundException("Backup file was not found for cloud upload.", localFilePath);
        }

        var bucketName = GetConfiguredValue(_options.BucketName)!;
        var objectName = BuildObjectName(fileName);

        await using var fileStream = File.OpenRead(localFilePath);
        var client = CreateClient();
        await client.UploadObjectAsync(
            bucketName,
            objectName,
            "application/zip",
            fileStream,
            cancellationToken: cancellationToken);

        logger.LogInformation(
            "Uploaded backup {FileName} to Google Cloud Storage bucket {BucketName} as {ObjectName}",
            fileName,
            bucketName,
            objectName);

        return $"gs://{bucketName}/{objectName}";
    }

    private StorageClient CreateClient()
    {
        var serviceAccountJson = GetConfiguredValue(_options.ServiceAccountJson);
        if (!string.IsNullOrWhiteSpace(serviceAccountJson))
        {
            var credential = CredentialFactory
                .FromJson<ServiceAccountCredential>(serviceAccountJson)
                .ToGoogleCredential();
            return StorageClient.Create(credential);
        }

        return StorageClient.Create();
    }

    private string? GetUnavailableReason()
    {
        var provider = GetConfiguredValue(_options.Provider);
        if (!string.Equals(provider, "GoogleCloudStorage", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(provider, "Google", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(provider, "GCS", StringComparison.OrdinalIgnoreCase))
        {
            return "Google Cloud Storage backup upload is not configured.";
        }

        if (string.IsNullOrWhiteSpace(GetConfiguredValue(_options.BucketName)))
        {
            return "Google Cloud Storage bucket name is not configured.";
        }

        var serviceAccountJson = GetConfiguredValue(_options.ServiceAccountJson);
        var credentialsPath = GetConfiguredValue(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS"));

        if (string.IsNullOrWhiteSpace(serviceAccountJson) && string.IsNullOrWhiteSpace(credentialsPath))
        {
            return "Google Cloud Storage credentials are not configured.";
        }

        return null;
    }

    private string BuildObjectName(string fileName)
    {
        var prefix = GetConfiguredValue(_options.ObjectPrefix) ?? "backups";
        prefix = prefix.Trim('/');

        return string.IsNullOrWhiteSpace(prefix)
            ? Path.GetFileName(fileName)
            : $"{prefix}/{Path.GetFileName(fileName)}";
    }

    private static string? GetConfiguredValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal)
            ? null
            : trimmed;
    }
}