using System.Net.Http.Json;
using Blazored.LocalStorage;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IVendingService
{
    /// <summary>
    /// Depots, cashier accounts and vendors. Throws rather than returning an empty overview, because
    /// "no depots" and "the call failed" must not look the same on a page someone manages from.
    /// </summary>
    Task<VendingOverviewModel> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a vendor, or saves one's details. Returns the saved vendor, or the server's refusal.</summary>
    Task<(RouteCustomerModel? Vendor, string? Error)> SaveVendorAsync(
        RouteCustomerModel vendor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a vendor, or restores a removed one. Removal is the API's soft delete: the row and its
    /// sales stay, and the till refuses the vendor from then on.
    /// </summary>
    Task<string?> SetVendorActiveAsync(
        RouteCustomerModel vendor,
        bool isActive,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The portal's client for the vending page.
/// </summary>
/// <remarks>
/// One read of its own — <c>api/vending/overview</c> — and the writes of <c>api/route-customers</c>,
/// which is where vendors have always been kept and where the rules on who may touch whose list live.
/// Checked by hand against <c>VendingController</c> and <c>RouteCustomersController</c>.
/// </remarks>
public class VendingService(
    HttpClient httpClient,
    ILogger<VendingService> logger,
    ILocalStorageService localStorage,
    CustomAuthStateProvider authStateProvider) : IVendingService
{
    public async Task<VendingOverviewModel> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAuthenticatedAsync(() => httpClient.GetAsync("api/vending/overview", cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error fetching the vending overview");
            throw new InvalidOperationException("The vending overview could not be loaded. Try again shortly.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var problem = await ReadProblemDetailAsync(response, cancellationToken);
                logger.LogWarning("GET api/vending/overview failed with {Status}: {Detail}", (int)response.StatusCode, problem);
                throw new InvalidOperationException(problem);
            }

            return await response.Content.ReadFromJsonAsync<VendingOverviewModel>(cancellationToken)
                ?? throw new InvalidOperationException("The server returned an empty vending overview.");
        }
    }

    public async Task<(RouteCustomerModel? Vendor, string? Error)> SaveVendorAsync(
        RouteCustomerModel vendor,
        CancellationToken cancellationToken = default)
    {
        var body = new UpdateRouteCustomerRequest
        {
            AssignedBusinessPartnerCode = vendor.AssignedBusinessPartnerCode,
            // Blank on a new vendor lets the server generate one from the name.
            Code = string.IsNullOrWhiteSpace(vendor.Code) ? null : vendor.Code.Trim(),
            Name = vendor.Name,
            Surname = vendor.Surname,
            Phone = vendor.Phone,
            Email = vendor.Email,
            Address = vendor.Address,
            VatNumber = vendor.VatNumber,
            IsActive = vendor.IsActive
        };

        try
        {
            using var response = vendor.Id > 0
                ? await SendAuthenticatedAsync(() => httpClient.PutAsJsonAsync($"api/route-customers/{vendor.Id}", body, cancellationToken))
                : await SendAuthenticatedAsync(() => httpClient.PostAsJsonAsync("api/route-customers", body, cancellationToken));

            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<RouteCustomerModel>(cancellationToken), null);
            }

            var problem = await ReadProblemDetailAsync(response, cancellationToken);
            logger.LogWarning("Saving vendor {Code} failed with {Status}: {Detail}", vendor.Code, (int)response.StatusCode, problem);
            return (null, problem);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error saving vendor {Code}", vendor.Code);
            return (null, $"The vendor could not be saved: {ex.Message}");
        }
    }

    public async Task<string?> SetVendorActiveAsync(
        RouteCustomerModel vendor,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        if (isActive)
        {
            // There is no restore endpoint; a restore is the same row saved active again.
            var copy = Copy(vendor);
            copy.IsActive = true;
            var (_, error) = await SaveVendorAsync(copy, cancellationToken);
            return error;
        }

        try
        {
            using var response = await SendAuthenticatedAsync(() => httpClient.DeleteAsync($"api/route-customers/{vendor.Id}", cancellationToken));
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var problem = await ReadProblemDetailAsync(response, cancellationToken);
            logger.LogWarning("Removing vendor {Code} failed with {Status}: {Detail}", vendor.Code, (int)response.StatusCode, problem);
            return problem;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error removing vendor {Code}", vendor.Code);
            return $"The vendor could not be removed: {ex.Message}";
        }
    }

    /// <summary>A detached copy, so an edit in progress never touches the row on screen.</summary>
    public static RouteCustomerModel Copy(RouteCustomerModel vendor) => new()
    {
        Id = vendor.Id,
        AssignedBusinessPartnerCode = vendor.AssignedBusinessPartnerCode,
        Code = vendor.Code,
        Name = vendor.Name,
        Surname = vendor.Surname,
        Phone = vendor.Phone,
        Email = vendor.Email,
        Address = vendor.Address,
        VatNumber = vendor.VatNumber,
        IsActive = vendor.IsActive,
        CreatedByUserId = vendor.CreatedByUserId,
        CreatedByUserName = vendor.CreatedByUserName,
        CreatedAt = vendor.CreatedAt,
        UpdatedAt = vendor.UpdatedAt
    };

    /// <summary>
    /// As the signed-in user, not the portal's API key: the overview is role-gated and adding a vendor
    /// records who added it, and neither can be answered for a key.
    /// </summary>
    private Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> sendAsync)
        => ApiTokenAuthentication.SendAsync(httpClient, authStateProvider, localStorage, sendAsync, logger);

    private static async Task<string> ReadProblemDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden)
        {
            return "Your account is not allowed to make this change.";
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemShape>(cancellationToken);

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

    private sealed class ProblemShape
    {
        public string? Title { get; set; }
        public string? Detail { get; set; }
    }
}
