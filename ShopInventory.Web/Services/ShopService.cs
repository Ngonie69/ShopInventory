using System.Net.Http.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IShopService
{
    /// <summary>Shops, closed ones excluded unless asked for.</summary>
    Task<List<ShopDto>> GetShopsAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a shop. Returns the saved shop, or the server's refusal.</summary>
    Task<(ShopDto? Shop, string? Error)> SaveShopAsync(ShopDto shop, CancellationToken cancellationToken = default);

    /// <summary>Closes a shop, or reopens a closed one. Returns the server's refusal if it declines.</summary>
    Task<(ShopDto? Shop, string? Error)> SetShopActiveAsync(int shopId, bool isActive, CancellationToken cancellationToken = default);
}

/// <summary>
/// The portal's client for the shops endpoints.
/// </summary>
/// <remarks>
/// The URLs are hand-written strings against a controller in another project, checked against
/// <c>ShopsController</c>: <c>api/shops</c>, <c>api/shops/{id}</c> and <c>api/shops/{id}/active</c>.
/// A read that fails returns an empty list and logs, so a wrong URL leaves a trail rather than
/// reporting "no shops" as though none had been opened. A write that fails never looks like success —
/// the server refuses for reasons a person can act on (a duplicate code, a warehouse another shop
/// already uses, operators still assigned) and those messages are surfaced verbatim.
/// </remarks>
public class ShopService(HttpClient httpClient, ILogger<ShopService> logger) : IShopService
{
    public async Task<List<ShopDto>> GetShopsAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"api/shops?includeInactive={includeInactive.ToString().ToLowerInvariant()}";
            return await httpClient.GetFromJsonAsync<List<ShopDto>>(url, cancellationToken) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching shops");
            return [];
        }
    }

    public async Task<(ShopDto? Shop, string? Error)> SaveShopAsync(
        ShopDto shop,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Two payload shapes on purpose, matching the two request DTOs. An update carries no code
            // — the code is what sales history groups on, so a shop that needs a different one is a
            // new shop — and no IsActive, which has its own endpoint because closing has a rule.
            HttpResponseMessage response;

            if (shop.Id > 0)
            {
                response = await httpClient.PutAsJsonAsync(
                    $"api/shops/{shop.Id}",
                    new
                    {
                        name = shop.Name,
                        businessPartnerCode = shop.BusinessPartnerCode,
                        warehouseCode = shop.WarehouseCode,
                        costCentreCode = shop.CostCentreCode
                    },
                    cancellationToken);
            }
            else
            {
                response = await httpClient.PostAsJsonAsync(
                    "api/shops",
                    new
                    {
                        code = shop.Code,
                        name = shop.Name,
                        businessPartnerCode = shop.BusinessPartnerCode,
                        warehouseCode = shop.WarehouseCode,
                        costCentreCode = shop.CostCentreCode
                    },
                    cancellationToken);
            }

            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<ShopDto>(cancellationToken), null);
            }

            var problem = await ReadProblemDetailAsync(response, cancellationToken);

            logger.LogWarning(
                "Saving shop {Code} failed with {Status}: {Detail}",
                shop.Code, (int)response.StatusCode, problem);

            return (null, problem);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error saving shop {Code}", shop.Code);
            return (null, $"The shop could not be saved: {ex.Message}");
        }
    }

    public async Task<(ShopDto? Shop, string? Error)> SetShopActiveAsync(
        int shopId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"api/shops/{shopId}/active?isActive={isActive.ToString().ToLowerInvariant()}";
            var response = await httpClient.PutAsync(url, content: null, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<ShopDto>(cancellationToken), null);
            }

            var problem = await ReadProblemDetailAsync(response, cancellationToken);

            logger.LogWarning(
                "Setting shop {ShopId} active={IsActive} failed with {Status}: {Detail}",
                shopId, isActive, (int)response.StatusCode, problem);

            return (null, problem);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting shop {ShopId} active state", shopId);
            return (null, $"The shop could not be updated: {ex.Message}");
        }
    }

    private static async Task<string> ReadProblemDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
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
