using System.Net;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IOfflineSigningLeaseService
{
    Task<List<FiscalDeviceOfflineLeaseSummaryResponse>?> GetOverviewAsync();

    Task<OfflineSigningLeaseAssignResult> AssignAsync(int deviceId, Guid? holderUserId, bool force);

    /// <summary>Active van accounts, and the device each already carries.</summary>
    Task<List<FiscalDeviceHandsetResponse>?> GetHandsetsAsync();

    /// <summary>What the platform says about a device, and whether it may be given to a van.</summary>
    Task<FiscalDevicePreviewResult> PreviewDeviceAsync(int deviceId, Guid? handsetUserId);

    /// <summary>Registers the handset that signs as a device, or releases it when the handset is null.</summary>
    Task<FiscalDevicePreviewResult> RegisterHandsetAsync(int deviceId, Guid? handsetUserId, bool force);
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

    public async Task<List<FiscalDeviceHandsetResponse>?> GetHandsetsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<FiscalDeviceHandsetResponse>>(
                "api/fiscal-devices/handsets");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching handsets that can carry a fiscal device");
            return null;
        }
    }

    public Task<FiscalDevicePreviewResult> PreviewDeviceAsync(int deviceId, Guid? handsetUserId)
    {
        var route = handsetUserId is { } handset
            ? $"api/fiscal-devices/{deviceId}/preview?handsetUserId={handset}"
            : $"api/fiscal-devices/{deviceId}/preview";

        return SendAsync(
            () => _httpClient.GetAsync(route),
            deviceId,
            "We couldn't read that device from the Fiscalisation platform. Please try again.");
    }

    /// <summary>
    /// Registers or releases the device.
    /// </summary>
    /// <remarks>
    /// Shares <see cref="SendAsync"/> with the preview because the API answers both with the same
    /// document — a save returns the device as it now stands, so the screen repaints from the outcome
    /// rather than from what it hoped the outcome would be.
    /// </remarks>
    public Task<FiscalDevicePreviewResult> RegisterHandsetAsync(int deviceId, Guid? handsetUserId, bool force)
    {
        var route = $"api/fiscal-devices/{deviceId}/handset{(force ? "?force=true" : string.Empty)}";

        return SendAsync(
            () => _httpClient.PutAsJsonAsync(
                route,
                new RegisterFiscalDeviceHandsetBody { HandsetUserId = handsetUserId }),
            deviceId,
            "We couldn't change that device's handset right now. Please try again.");
    }

    /// <summary>
    /// Runs a call that answers with a device preview, keeping a 409 distinguishable from a failure.
    /// </summary>
    /// <remarks>
    /// A 409 means the handset losing the device is still carrying signed receipts — the same question
    /// the nomination screen asks, and it is surfaced the same way so the office reads the consequence
    /// before repeating the call with force.
    /// </remarks>
    private async Task<FiscalDevicePreviewResult> SendAsync(
        Func<Task<HttpResponseMessage>> send,
        int deviceId,
        string fallbackMessage)
    {
        try
        {
            var response = await send();

            if (response.IsSuccessStatusCode)
            {
                return new FiscalDevicePreviewResult
                {
                    Success = true,
                    Device = await response.Content.ReadFromJsonAsync<FiscalDevicePreviewResponse>()
                };
            }

            var error = await response.Content.ReadAsStringAsync();

            _logger.LogWarning(
                "Fiscal device {DeviceId} call failed: {StatusCode} - {Error}",
                deviceId, response.StatusCode, error);

            return new FiscalDevicePreviewResult
            {
                Success = false,
                NeedsForce = response.StatusCode == HttpStatusCode.Conflict,
                Message = ApiErrorResponse.GetFriendlyMessage(response.StatusCode, error, fallbackMessage)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error on fiscal device {DeviceId}", deviceId);

            return new FiscalDevicePreviewResult
            {
                Success = false,
                Message = ApiErrorResponse.GetFriendlyMessage(ex, fallbackMessage)
            };
        }
    }
}

// Mirrors of the API's DTOs. Nullability has to match the API side exactly — a non-nullable property
// here against a null on the wire throws in System.Text.Json and the page reports no data at all.

public class FiscalDeviceHandsetResponse
{
    public Guid UserId { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    /// <summary>What this handset already signs as, or null if it has never been registered.</summary>
    public int? FiscalDeviceId { get; set; }
}

public class FiscalDeviceRegistrationFindingResponse
{
    /// <summary><c>Note</c>, <c>Warn</c> or <c>Block</c>.</summary>
    public string Severity { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

public class FiscalDevicePreviewResponse
{
    public int DeviceId { get; set; }

    public bool Reachable { get; set; }

    public string? PlatformError { get; set; }

    public string? SerialNumber { get; set; }

    public string? BranchName { get; set; }

    public string? OperatingMode { get; set; }

    public string? TaxPayerName { get; set; }

    public DateTime? CertificateValidTill { get; set; }

    public int? CertificateDaysRemaining { get; set; }

    public int? FiscalDayNo { get; set; }

    public string? FiscalDayStatus { get; set; }

    public Guid? CurrentHolderUserId { get; set; }

    public string? CurrentHolderLabel { get; set; }

    public int PinnedDefaultDeviceId { get; set; }

    public bool CanRegister { get; set; }

    /// <summary>Whether the device can be taken off whoever holds it. Not gated on the platform.</summary>
    public bool CanRelease { get; set; }

    public List<FiscalDeviceRegistrationFindingResponse> Findings { get; set; } = [];
}

public class RegisterFiscalDeviceHandsetBody
{
    public Guid? HandsetUserId { get; set; }
}

public class FiscalDevicePreviewResult
{
    public bool Success { get; set; }

    /// <summary>The API refused because the outgoing handset is still carrying signed receipts.</summary>
    public bool NeedsForce { get; set; }

    public string Message { get; set; } = string.Empty;

    public FiscalDevicePreviewResponse? Device { get; set; }
}

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
