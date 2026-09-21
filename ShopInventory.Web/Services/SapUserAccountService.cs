using System.Net.Http.Json;
using System.Text.Json;
using Blazored.LocalStorage;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>SAP Business One user accounts, as the back office administers them.</summary>
public interface ISapUserAccountService
{
    /// <summary>The SAP user accounts, optionally narrowed to a search or to the locked ones.</summary>
    Task<SapUserAccountListModel> GetAccountsAsync(
        string? search,
        bool lockedOnly,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear SAP's lock on one account. Throws with the API's reason — which is usually SAP's own —
    /// so the page can show it.
    /// </summary>
    Task<SapUserAccountModel> UnlockAsync(int internalKey, CancellationToken cancellationToken = default);

    /// <summary>Set a new password on one account. Throws with the API's reason.</summary>
    Task<SapUserAccountModel> ChangePasswordAsync(
        int internalKey,
        string newPassword,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// Follows <see cref="VanSalesCustomerAccountService"/>: the read answers an empty list on failure so
/// the screen still renders, and the writes throw. The asymmetry earns its keep here — an
/// administrator who presses Unlock and sees nothing happen tells the person to try again, and the
/// reason SAP refused (most often that the Service Layer account is not a superuser) is exactly what
/// has to reach them instead.
/// <para>
/// The new password is sent in the request body and nowhere else. It is not written to local
/// storage, not put in a query string, and not logged on either side.
/// </para>
/// </remarks>
public class SapUserAccountService(
    HttpClient httpClient,
    ILogger<SapUserAccountService> logger,
    ILocalStorageService localStorage,
    CustomAuthStateProvider authStateProvider
) : ISapUserAccountService
{
    private const string BaseUrl = "api/sap-users";

    public async Task<SapUserAccountListModel> GetAccountsAsync(
        string? search,
        bool lockedOnly,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = BuildQuery(
                ("search", string.IsNullOrWhiteSpace(search) ? null : search.Trim()),
                ("lockedOnly", lockedOnly ? "true" : null));

            using var response = await SendAuthenticatedAsync(
                () => httpClient.GetAsync($"{BaseUrl}{query}", cancellationToken));

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<SapUserAccountListModel>(cancellationToken)
                   ?? new SapUserAccountListModel();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading SAP user accounts");
            return new SapUserAccountListModel();
        }
    }

    public async Task<SapUserAccountModel> UnlockAsync(int internalKey, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () => httpClient.PostAsync($"{BaseUrl}/{internalKey}/unlock", content: null, cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                await ExtractErrorMessageAsync(response, "The account could not be unlocked."));
        }

        return await response.Content.ReadFromJsonAsync<SapUserAccountModel>(cancellationToken)
               ?? throw new InvalidOperationException("The account could not be unlocked.");
    }

    public async Task<SapUserAccountModel> ChangePasswordAsync(
        int internalKey,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () => httpClient.PostAsJsonAsync(
                $"{BaseUrl}/{internalKey}/password",
                new { newPassword },
                cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                await ExtractErrorMessageAsync(response, "The password could not be changed."));
        }

        return await response.Content.ReadFromJsonAsync<SapUserAccountModel>(cancellationToken)
               ?? throw new InvalidOperationException("The password could not be changed.");
    }

    private Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> sendAsync)
        => ApiTokenAuthentication.SendAsync(httpClient, authStateProvider, localStorage, sendAsync, logger);

    private static string BuildQuery(params (string Name, string? Value)[] parameters)
    {
        var pairs = parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}")
            .ToList();

        return pairs.Count == 0 ? string.Empty : "?" + string.Join("&", pairs);
    }

    /// <summary>
    /// The API's own explanation, or a fallback.
    /// </summary>
    /// <remarks>
    /// Reads <c>detail</c> first, which is where the sentence lives for everything these two routes
    /// refuse on — SAP's password-policy message, its refusal of a non-superuser session, "not
    /// locked", "no such account". Falls back to the <c>errors</c> dictionary because an all-Validation
    /// answer puts the generic "The request contains validation errors." in <c>detail</c> and the
    /// sentence that matters in there; without the fallback a rejected password shape would reach the
    /// administrator as a sentence that names nothing to fix.
    /// </remarks>
    private static async Task<string> ExtractErrorMessageAsync(HttpResponseMessage response, string fallback)
    {
        var content = await response.Content.ReadAsStringAsync();

        if (string.IsNullOrWhiteSpace(content))
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = errors.EnumerateObject()
                    .SelectMany(property => property.Value.ValueKind == JsonValueKind.Array
                        ? property.Value.EnumerateArray().Select(item => item.GetString())
                        : [property.Value.GetString()])
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .ToList();

                if (messages.Count > 0)
                {
                    return string.Join(" ", messages);
                }
            }

            if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
            {
                var message = detail.GetString();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
        }
        catch (JsonException)
        {
            // Not problem details — a proxy page, most likely. The fallback still says something.
        }

        return fallback;
    }
}
