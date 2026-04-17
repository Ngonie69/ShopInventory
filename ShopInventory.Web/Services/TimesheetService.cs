using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface ITimesheetService
{
    Task<TimesheetListResponse?> GetTimesheetsAsync(int page = 1, int pageSize = 20,
        Guid? userId = null, string? username = null, string? customerCode = null,
        DateTime? fromDate = null, DateTime? toDate = null);
    Task<TimesheetReportResponse?> GetReportAsync(Guid? userId = null, string? username = null,
        DateTime? fromDate = null, DateTime? toDate = null);
}

public class TimesheetService : ITimesheetService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TimesheetService> _logger;

    public TimesheetService(HttpClient httpClient, ILogger<TimesheetService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<TimesheetListResponse?> GetTimesheetsAsync(int page = 1, int pageSize = 20,
        Guid? userId = null, string? username = null, string? customerCode = null,
        DateTime? fromDate = null, DateTime? toDate = null)
    {
        try
        {
            var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}" };
            if (userId.HasValue) queryParams.Add($"userId={userId.Value}");
            if (!string.IsNullOrEmpty(username)) queryParams.Add($"username={Uri.EscapeDataString(username)}");
            if (!string.IsNullOrEmpty(customerCode)) queryParams.Add($"customerCode={Uri.EscapeDataString(customerCode)}");
            if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-ddTHH:mm:ss}");
            if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-ddTHH:mm:ss}");

            var url = $"api/Timesheet?{string.Join("&", queryParams)}";
            return await _httpClient.GetFromJsonAsync<TimesheetListResponse>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching timesheets");
            return null;
        }
    }

    public async Task<TimesheetReportResponse?> GetReportAsync(Guid? userId = null, string? username = null,
        DateTime? fromDate = null, DateTime? toDate = null)
    {
        try
        {
            var queryParams = new List<string>();
            if (userId.HasValue) queryParams.Add($"userId={userId.Value}");
            if (!string.IsNullOrEmpty(username)) queryParams.Add($"username={Uri.EscapeDataString(username)}");
            if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-ddTHH:mm:ss}");
            if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-ddTHH:mm:ss}");

            var url = queryParams.Count > 0
                ? $"api/Timesheet/report?{string.Join("&", queryParams)}"
                : "api/Timesheet/report";

            return await _httpClient.GetFromJsonAsync<TimesheetReportResponse>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching timesheet report");
            return null;
        }
    }
}
