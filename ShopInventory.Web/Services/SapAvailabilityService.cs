using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface ISapAvailabilityService
{
    /// <summary>The cluster's view of SAP, or null when the API could not be asked.</summary>
    Task<SapAvailabilityResponse?> GetAsync(CancellationToken cancellationToken = default);
}

public class SapAvailabilityService(
    HttpClient httpClient,
    ILogger<SapAvailabilityService> logger) : ISapAvailabilityService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<SapAvailabilityResponse?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(Timeout);

            return await httpClient.GetFromJsonAsync<SapAvailabilityResponse>(
                "api/sync/sap-availability", budget.Token);
        }
        catch (Exception ex)
        {
            // A banner that could not be fetched must not become an error on a page that is otherwise
            // fine. Null leaves the banner as it last was.
            logger.LogDebug(ex, "Could not read SAP availability");
            return null;
        }
    }
}

/// <summary>
/// Mirrors the API's <c>SapAvailabilityResult</c>. Nullability matches it field for field: a mismatch
/// deserialises as a default and the banner would quietly say nothing.
/// </summary>
public class SapAvailabilityResponse
{
    public bool IsDown { get; set; }

    /// <summary><c>Unreachable</c> or <c>SwitchedOff</c>.</summary>
    public string? Cause { get; set; }

    public DateTime? SinceUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    public int SalesAwaitingSap { get; set; }
}
