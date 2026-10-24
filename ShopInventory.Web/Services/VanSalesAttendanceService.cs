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
    Task<ReportResult<VanVisitListResponse>> GetVisitsAsync(int page = 1, int pageSize = 20,
        Guid? userId = null, string? username = null, string? customerCode = null,
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken cancellationToken = default);

    /// <param name="fromDate">A CAT trading day, not an instant — sent as a plain date.</param>
    /// <param name="toDate">A CAT trading day, inclusive.</param>
    /// <param name="userId">One van rep, by id. Omitted, the report covers every rep.</param>
    /// <param name="username">
    /// The same narrowing for a caller holding the name rather than the id — a screen that has the
    /// rep's name off a row, not a lookup. An alternative to the id, not a companion to it.
    /// </param>
    Task<ReportResult<VanVisitReportResponse>> GetReportAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        Guid? userId = null, string? username = null,
        CancellationToken cancellationToken = default);
}

public class VanSalesAttendanceService(
    HttpClient httpClient,
    ILogger<VanSalesAttendanceService> logger
) : IVanSalesAttendanceService
{
    /// <summary>Reads one report, or the reason it could not be read. See <see cref="ReportReader"/>.</summary>
    private Task<ReportResult<T>> GetReportAsync<T>(string url, string what, CancellationToken cancellationToken)
        where T : class =>
        ReportReader.GetAsync<T>(httpClient, logger, url, what, cancellationToken);

    public async Task<ReportResult<VanVisitListResponse>> GetVisitsAsync(int page = 1, int pageSize = 20,
        Guid? userId = null, string? username = null, string? customerCode = null,
        DateTime? fromDate = null, DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (userId.HasValue) queryParams.Add($"userId={userId.Value}");
        if (!string.IsNullOrEmpty(username)) queryParams.Add($"username={Uri.EscapeDataString(username)}");
        if (!string.IsNullOrEmpty(customerCode)) queryParams.Add($"customerCode={Uri.EscapeDataString(customerCode)}");
        if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-ddTHH:mm:ss}");
        if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-ddTHH:mm:ss}");

        var url = $"api/van-sales/visits?{string.Join("&", queryParams)}";
        return await GetReportAsync<VanVisitListResponse>(url, "van activity", cancellationToken);
    }

    public async Task<ReportResult<VanVisitReportResponse>> GetReportAsync(
        DateTime? fromDate = null, DateTime? toDate = null,
        Guid? userId = null, string? username = null,
        CancellationToken cancellationToken = default)
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

        return await GetReportAsync<VanVisitReportResponse>(url, "attendance report", cancellationToken);
    }
}
