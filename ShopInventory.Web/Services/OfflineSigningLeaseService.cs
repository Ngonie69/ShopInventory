using System.Net;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IOfflineSigningLeaseService
{
    Task<List<FiscalDeviceOfflineLeaseSummaryResponse>?> GetOverviewAsync();

    Task<OfflineSigningLeaseAssignResult> AssignAsync(int deviceId, Guid? holderUserId, bool force);
}

/// <summary>
/// The office's end of "which van may sign receipts offline today".
/// </summary>
public class OfflineSigningLeaseService : IOfflineSigningLeaseService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OfflineSigningLeaseService> _logger;

    public OfflineSigningLeaseService(HttpClient httpClient, ILogger<OfflineSigningLeaseService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<FiscalDeviceOfflineLeaseSummaryResponse>?> GetOverviewAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<FiscalDeviceOfflineLeaseSummaryResponse>>(
                "api/fiscal-devices/offline-leases");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching offline signing leases");
            return null;
        }
    }

    /// <summary>
    /// Moves offline signing, or clears it when <paramref name="holderUserId"/> is null.
    /// </summary>
    /// <remarks>
    /// A 409 is the interesting answer, not a failure: the outgoing handset is still carrying signed
    /// receipts, and the API is asking whether the office really means it. That is surfaced as
    /// <see cref="OfflineSigningLeaseAssignResult.NeedsForce"/> so the page can put the consequence in
    /// front of someone before repeating the call with force.
    /// </remarks>
    public async Task<OfflineSigningLeaseAssignResult> AssignAsync(int deviceId, Guid? holderUserId, bool force)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync(
                $"api/fiscal-devices/{deviceId}/offline-lease",
                new AssignOfflineSigningLeaseBody { HolderUserId = holderUserId, Force = force });

            if (response.IsSuccessStatusCode)
            {
                return new OfflineSigningLeaseAssignResult
                {
                    Success = true,
                    Lease = await response.Content.ReadFromJsonAsync<FiscalDeviceOfflineLeaseResponse>()
                };
            }

            var error = await response.Content.ReadAsStringAsync();

            _logger.LogWarning(
                "Failed to move offline signing on device {DeviceId}: {StatusCode} - {Error}",
                deviceId, response.StatusCode, error);

            return new OfflineSigningLeaseAssignResult
            {
                Success = false,
                NeedsForce = response.StatusCode == HttpStatusCode.Conflict,
                Message = ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode,
                    error,
                    "We couldn't change offline signing right now. Please try again.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing offline signing on device {DeviceId}", deviceId);

            return new OfflineSigningLeaseAssignResult
            {
                Success = false,
                Message = ApiErrorResponse.GetFriendlyMessage(
                    ex,
                    "We couldn't change offline signing right now. Please try again.")
            };
        }
    }
}

// Mirrors of the API's DTOs. Nullability has to match the API side exactly — a non-nullable property
// here against a null on the wire throws in System.Text.Json and the page reports no data at all.

public class FiscalDeviceOfflineLeaseSummaryResponse
{
    public FiscalDeviceOfflineLeaseResponse Lease { get; set; } = new();

    public List<OfflineSigningCandidateResponse> Candidates { get; set; } = [];
}

public class FiscalDeviceOfflineLeaseResponse
{
    public int DeviceId { get; set; }

    /// <summary>Null when nobody is nominated, which means no van may trade out of coverage.</summary>
    public Guid? HolderUserId { get; set; }

    public string? HolderLabel { get; set; }

    public DateTime? AssignedAtUtc { get; set; }

    public string? AssignedByName { get; set; }

    /// <summary>Null means the handset has not reported, which is not the same as none.</summary>
    public int? HolderPendingSales { get; set; }

    public DateTime? HolderLastSeenAtUtc { get; set; }

    public bool CanHandOver { get; set; }
}

public class OfflineSigningCandidateResponse
{
    public Guid UserId { get; set; }

    public string Label { get; set; } = string.Empty;
}

public class AssignOfflineSigningLeaseBody
{
    public Guid? HolderUserId { get; set; }

    public bool Force { get; set; }
}

public class OfflineSigningLeaseAssignResult
{
    public bool Success { get; set; }

    /// <summary>The API refused because the outgoing handset is still carrying signed receipts.</summary>
    public bool NeedsForce { get; set; }

    public string Message { get; set; } = string.Empty;

    public FiscalDeviceOfflineLeaseResponse? Lease { get; set; }
}
