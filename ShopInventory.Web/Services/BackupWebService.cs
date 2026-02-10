using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IBackupService
{
    Task<BackupListResponse?> GetAllBackupsAsync();
    Task<BackupDto?> GetBackupByIdAsync(int id);
    Task<BackupDto?> CreateBackupAsync(CreateBackupRequest request);
    Task<bool> RestoreBackupAsync(int backupId);
    Task<bool> DeleteBackupAsync(int id);
    Task<BackupStatsDto?> GetStatsAsync();
    Task<Stream?> DownloadBackupAsync(int id);
}

public class BackupService : IBackupService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BackupService> _logger;

    public BackupService(HttpClient httpClient, ILogger<BackupService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<BackupListResponse?> GetAllBackupsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<BackupListResponse>("api/backup");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching backups");
            return null;
        }
    }

    public async Task<BackupDto?> GetBackupByIdAsync(int id)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<BackupDto>($"api/backup/{id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching backup {Id}", id);
            return null;
        }
    }

    public async Task<BackupDto?> CreateBackupAsync(CreateBackupRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("api/backup", request);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<BackupDto>();
            }
            _logger.LogWarning("Failed to create backup: {StatusCode}", response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating backup");
            return null;
        }
    }

    public async Task<bool> RestoreBackupAsync(int backupId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/backup/{backupId}/restore", null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring backup {Id}", backupId);
            return false;
        }
    }

    public async Task<bool> DeleteBackupAsync(int id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/backup/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting backup {Id}", id);
            return false;
        }
    }

    public async Task<BackupStatsDto?> GetStatsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<BackupStatsDto>("api/backup/stats");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching backup stats");
            return null;
        }
    }

    public async Task<Stream?> DownloadBackupAsync(int id)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/backup/{id}/download");
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStreamAsync();
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading backup {Id}", id);
            return null;
        }
    }
}
