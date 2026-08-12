using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

/// <summary>
/// Reads van sales check-in and check-out.
///
/// Its own service rather than a channel argument on <see cref="ITimesheetService"/>. That argument
/// was optional, which meant a caller that omitted it got both operations' rows — and the timesheet
/// report, which never passed one, counted van calls as merchandiser visits for as long as it
/// existed. Nothing here can return a merchandiser visit, and nothing on ITimesheetService can return
/// a van call.
/// </summary>
public interface IVanSalesAttendanceService
{
    Task<VanVisitListResponse?> GetVisitsAsync(int page = 1, int pageSize = 20,
        Guid? userId = null, string? username = null, string? customerCode = null,
        DateTime? fromDate = null, DateTime? toDate = null);

    /// <param name="fromDate">A CAT trading day, not an instant — sent as a plain date.</param>
    /// <param name="toDate">A CAT trading day, inclusive.</param>
    Task<VanVisitReportResponse?> GetReportAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        Guid? userId = null, string? username = null);
}

public class VanSalesAttendanceService(
    HttpClient httpClient,
    ILogger<VanSalesAttendanceService> logger
) : IVanSalesAttendanceService
{
    public async Task<VanVisitListResponse?> GetVisitsAsync(int page = 1, int pageSize = 20,
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

            var url = $"api/van-sales/visits?{string.Join("&", queryParams)}";
            return await httpClient.GetFromJsonAsync<VanVisitListResponse>(url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching van sales visits");
            return null;
        }
    }

    public async Task<VanVisitReportResponse?> GetReportAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        Guid? userId = null, string? username = null)
    {
        try
        {
            var queryParams = new List<string>();

            // Date only. These are CAT trading days and the API takes them as given; sending a time
            // component invites it to be read as an instant and shift the window by two hours.
            if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");
            if (userId.HasValue) queryParams.Add($"userId={userId.Value}");
            if (!string.IsNullOrEmpty(username)) queryParams.Add($"username={Uri.EscapeDataString(username)}");

            var url = queryParams.Count > 0
                ? $"api/van-sales/visits/report?{string.Join("&", queryParams)}"
                : "api/van-sales/visits/report";

            return await httpClient.GetFromJsonAsync<VanVisitReportResponse>(url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the van sales attendance report");
            return null;
        }
    }
}
