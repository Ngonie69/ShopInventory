using System.Net.Http.Json;
using Blazored.LocalStorage;

namespace ShopInventory.Web.Services;

public interface ISapAvailabilityService
{
    /// <summary>The cluster's view of SAP, or null when the API could not be asked.</summary>
    Task<SapAvailabilityResponse?> GetAsync(CancellationToken cancellationToken = default);
}

public class SapAvailabilityService(
    HttpClient httpClient,
    ILogger<SapAvailabilityService> logger,
    ILocalStorageService localStorage,
    CustomAuthStateProvider authStateProvider) : ISapAvailabilityService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<SapAvailabilityResponse?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(Timeout);

            return await GetAuthenticatedJsonAsync<SapAvailabilityResponse>(
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

    /// <summary>
    /// As the signed-in user for as long as the session can be renewed: a valid access token is
    /// attached first, and one the API rejects is renewed and the call sent once more.
    /// </summary>
    private Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> sendAsync)
        => ApiTokenAuthentication.SendAsync(httpClient, authStateProvider, localStorage, sendAsync, logger);

    /// <summary>The body deserialized, or the failure thrown, as <c>GetFromJsonAsync</c> has it.</summary>
    private async Task<T?> GetAuthenticatedJsonAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () => httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
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

    /// <summary>
    /// Why SAP is unavailable, or while <see cref="IsSlow"/> what is failing, as SL Health tells it. Null
    /// when it has nothing to add.
    /// </summary>
    public string? Explanation { get; set; }

    /// <summary>SAP answers the API, but SL Health has found its logins or queries failing for two minutes.</summary>
    public bool IsSlow { get; set; }

    public DateTime? SlowSinceUtc { get; set; }
}
