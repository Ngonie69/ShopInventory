using System.Globalization;
using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IVanSalesReportService
{
    Task<DepartureComplianceReportResponse?> GetComplianceReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? userId = null,
        string? routeCode = null);

    Task<VanSalesPerformanceReportResponse?> GetPerformanceReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? userId = null,
        string? routeCode = null,
        int topItems = 50);

    Task<VanSalesCoverageReportResponse?> GetCoverageReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? userId = null,
        string? routeCode = null,
        int lapseDays = 90,
        VanSalesCoverageGranularity granularity = VanSalesCoverageGranularity.Month);

    Task<VanReplenishmentReportResponse?> GetReplenishmentReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? vanWarehouseCode = null);

    Task<VanStockReportResponse?> GetStockReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? vanWarehouseCode = null,
        int deadStockDays = 14);

    Task<VanSalesExceptionsReportResponse?> GetExceptionsReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? userId = null);

    Task<VanMarginReportResponse?> GetMarginReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? userId = null,
        string? warehouseCode = null);

    Task<VanSalesScorecardReportResponse?> GetScorecardReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        VanSalesScorecardGrouping grouping = VanSalesScorecardGrouping.Rep,
        Guid? userId = null,
        double? callComplianceTarget = null,
        double? strikeRateTarget = null);

    Task<List<RouteDto>> GetRoutesAsync(bool includeInactive = false);

    /// <summary>Creates or updates a route. Returns the saved route, or the server's refusal.</summary>
    Task<(RouteDto? Route, string? Error)> SaveRouteAsync(RouteDto route);
}

/// <summary>
/// The portal's client for the van sales reporting endpoints.
/// </summary>
/// <remarks>
/// The URLs here are hand-written strings against a controller in another project, which is a known
/// way for this codebase to break quietly: a route that no longer exists returns 404, the catch below
/// swallows it, and the page reports "no data" as though the vans had a quiet month. Both paths are
/// checked against <c>VanSalesReportController</c> — <c>api/van-sales/compliance-report</c>,
/// <c>api/van-sales/performance-report</c>, <c>api/van-sales/coverage-report</c>,
/// <c>api/van-sales/replenishment-report</c>, <c>api/van-sales/stock-report</c>,
/// <c>api/van-sales/exceptions-report</c>, <c>api/van-sales/scorecard-report</c>,
/// <c>api/van-sales/margin-report</c> and
/// <c>api/van-sales/routes</c> — and the exception is
/// logged rather than only returned as null, so a wrong URL leaves a trail.
/// </remarks>
public class VanSalesReportService(HttpClient httpClient, ILogger<VanSalesReportService> logger)
    : IVanSalesReportService
{
    public async Task<DepartureComplianceReportResponse?> GetComplianceReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? userId = null,
        string? routeCode = null)
    {
        try
        {
            var queryParams = new List<string>();

            // Dates only: these are CAT trading days, not instants. Sending a time component would
            // invite the API to treat them as UTC and shift the window.
            if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");
            if (userId.HasValue) queryParams.Add($"userId={userId.Value}");
            if (!string.IsNullOrWhiteSpace(routeCode))
                queryParams.Add($"routeCode={Uri.EscapeDataString(routeCode)}");

            var url = queryParams.Count > 0
                ? $"api/van-sales/compliance-report?{string.Join("&", queryParams)}"
                : "api/van-sales/compliance-report";

            return await httpClient.GetFromJsonAsync<DepartureComplianceReportResponse>(url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the departure compliance report");
            return null;
        }
    }

    public async Task<VanSalesPerformanceReportResponse?> GetPerformanceReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? userId = null,
        string? routeCode = null,
        int topItems = 50)
    {
        try
        {
            // Dates only, for the reason given above: these are CAT trading days, not instants.
            var queryParams = new List<string> { $"topItems={topItems}" };

            if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");
            if (userId.HasValue) queryParams.Add($"userId={userId.Value}");
            if (!string.IsNullOrWhiteSpace(routeCode))
                queryParams.Add($"routeCode={Uri.EscapeDataString(routeCode)}");

            var url = $"api/van-sales/performance-report?{string.Join("&", queryParams)}";

            return await httpClient.GetFromJsonAsync<VanSalesPerformanceReportResponse>(url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the van sales performance report");
            return null;
        }
    }

    public async Task<VanSalesCoverageReportResponse?> GetCoverageReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? userId = null,
        string? routeCode = null,
        int lapseDays = 90,
        VanSalesCoverageGranularity granularity = VanSalesCoverageGranularity.Month)
    {
        try
        {
            var queryParams = new List<string>
            {
                $"lapseDays={lapseDays}",
                $"granularity={granularity}"
            };

            if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");
            if (userId.HasValue) queryParams.Add($"userId={userId.Value}");
            if (!string.IsNullOrWhiteSpace(routeCode))
                queryParams.Add($"routeCode={Uri.EscapeDataString(routeCode)}");

            var url = $"api/van-sales/coverage-report?{string.Join("&", queryParams)}";

            return await httpClient.GetFromJsonAsync<VanSalesCoverageReportResponse>(url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the van sales coverage report");
            return null;
        }
    }

    public async Task<VanReplenishmentReportResponse?> GetReplenishmentReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? vanWarehouseCode = null)
    {
        try
        {
            var queryParams = new List<string>();

            if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");
            if (!string.IsNullOrWhiteSpace(vanWarehouseCode))
                queryParams.Add($"vanWarehouseCode={Uri.EscapeDataString(vanWarehouseCode)}");

            var url = queryParams.Count > 0
                ? $"api/van-sales/replenishment-report?{string.Join("&", queryParams)}"
                : "api/van-sales/replenishment-report";

            return await httpClient.GetFromJsonAsync<VanReplenishmentReportResponse>(url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the van replenishment report");
            return null;
        }
    }

    public async Task<VanStockReportResponse?> GetStockReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        string? vanWarehouseCode = null,
        int deadStockDays = 14)
    {
        try
        {
            var queryParams = new List<string> { $"deadStockDays={deadStockDays}" };

            if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");
            if (!string.IsNullOrWhiteSpace(vanWarehouseCode))
                queryParams.Add($"vanWarehouseCode={Uri.EscapeDataString(vanWarehouseCode)}");

            var url = $"api/van-sales/stock-report?{string.Join("&", queryParams)}";

            return await httpClient.GetFromJsonAsync<VanStockReportResponse>(url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the van stock report");
            return null;
        }
    }

    public async Task<VanSalesExceptionsReportResponse?> GetExceptionsReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? userId = null)
    {
        try
        {
            var queryParams = new List<string>();

            // Dates only, for the reason given above: these are CAT trading days, not instants.
            if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");
            if (userId.HasValue) queryParams.Add($"userId={userId.Value}");

            var url = queryParams.Count > 0
                ? $"api/van-sales/exceptions-report?{string.Join("&", queryParams)}"
                : "api/van-sales/exceptions-report";

            return await httpClient.GetFromJsonAsync<VanSalesExceptionsReportResponse>(url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the van sales exceptions report");
            return null;
        }
    }

    public async Task<VanMarginReportResponse?> GetMarginReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Guid? userId = null,
        string? warehouseCode = null)
    {
        try
        {
            var queryParams = new List<string>();

            // Dates only, for the reason given above: these are CAT trading days, not instants.
            if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");
            if (userId.HasValue) queryParams.Add($"userId={userId.Value}");

            if (!string.IsNullOrWhiteSpace(warehouseCode))
            {
                queryParams.Add($"warehouseCode={Uri.EscapeDataString(warehouseCode.Trim())}");
            }

            var url = queryParams.Count > 0
                ? $"api/van-sales/margin-report?{string.Join("&", queryParams)}"
                : "api/van-sales/margin-report";

            return await httpClient.GetFromJsonAsync<VanMarginReportResponse>(url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the van margin report");
            return null;
        }
    }

    public async Task<VanSalesScorecardReportResponse?> GetScorecardReportAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        VanSalesScorecardGrouping grouping = VanSalesScorecardGrouping.Rep,
        Guid? userId = null,
        double? callComplianceTarget = null,
        double? strikeRateTarget = null)
    {
        try
        {
            var queryParams = new List<string>();

            // Dates only, for the reason given above: these are CAT trading days, not instants.
            if (fromDate.HasValue) queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue) queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");
            queryParams.Add($"grouping={grouping}");
            if (userId.HasValue) queryParams.Add($"userId={userId.Value}");

            // The targets are only sent when overridden, so the API's defaults stay the single
            // definition of what a rep is measured against.
            if (callComplianceTarget.HasValue)
            {
                queryParams.Add(
                    $"callComplianceTarget={callComplianceTarget.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            if (strikeRateTarget.HasValue)
            {
                queryParams.Add(
                    $"strikeRateTarget={strikeRateTarget.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            var url = $"api/van-sales/scorecard-report?{string.Join("&", queryParams)}";

            return await httpClient.GetFromJsonAsync<VanSalesScorecardReportResponse>(url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching the van sales scorecard report");
            return null;
        }
    }

    public async Task<List<RouteDto>> GetRoutesAsync(bool includeInactive = false)
    {
        try
        {
            var url = $"api/van-sales/routes?includeInactive={includeInactive.ToString().ToLowerInvariant()}";
            return await httpClient.GetFromJsonAsync<List<RouteDto>>(url) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching routes");
            return [];
        }
    }

    public async Task<(RouteDto? Route, string? Error)> SaveRouteAsync(RouteDto route)
    {
        try
        {
            var payload = new
            {
                code = route.Code,
                name = route.Name,
                territory = route.Territory,
                truckRegNo = route.TruckRegNo,
                isActive = route.IsActive
            };

            var response = route.Id > 0
                ? await httpClient.PutAsJsonAsync($"api/van-sales/routes/{route.Id}", payload)
                : await httpClient.PostAsJsonAsync("api/van-sales/routes", payload);

            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<RouteDto>(), null);
            }

            // The server refuses for reasons a person can act on — a duplicate code, or vans still on
            // a route being retired — so its message is surfaced rather than swallowed into a generic
            // failure. Unlike the read paths above, a failed save must never look like success.
            var problem = await ReadProblemDetailAsync(response);

            logger.LogWarning(
                "Saving route {Code} failed with {Status}: {Detail}",
                route.Code, (int)response.StatusCode, problem);

            return (null, problem);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error saving route {Code}", route.Code);
            return (null, $"The route could not be saved: {ex.Message}");
        }
    }

    private static async Task<string> ReadProblemDetailAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemShape>();

            if (!string.IsNullOrWhiteSpace(problem?.Detail))
            {
                return problem.Detail;
            }

            return string.IsNullOrWhiteSpace(problem?.Title)
                ? $"The server refused the change ({(int)response.StatusCode})."
                : problem.Title;
        }
        catch
        {
            return $"The server refused the change ({(int)response.StatusCode}).";
        }
    }

    /// <summary>Just enough of RFC 7807 to show the caller why. Mirrored by hand, like the rest.</summary>
    private sealed class ProblemShape
    {
        public string? Title { get; set; }
        public string? Detail { get; set; }
    }
}
