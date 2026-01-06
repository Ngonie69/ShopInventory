using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IBusinessPartnerService
{
    Task<BusinessPartnerListResponse?> GetBusinessPartnersAsync();
    Task<BusinessPartnerListResponse?> GetBusinessPartnersByTypeAsync(string cardType);
    Task<BusinessPartnerListResponse?> SearchBusinessPartnersAsync(string searchTerm);
    Task<BusinessPartnerDto?> GetBusinessPartnerByCodeAsync(string cardCode);
}

public class BusinessPartnerService : IBusinessPartnerService
{
    private readonly HttpClient _httpClient;

    public BusinessPartnerService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<BusinessPartnerListResponse?> GetBusinessPartnersAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<BusinessPartnerListResponse>("api/businesspartner");
        }
        catch
        {
            return null;
        }
    }

    public async Task<BusinessPartnerListResponse?> GetBusinessPartnersByTypeAsync(string cardType)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<BusinessPartnerListResponse>($"api/businesspartner/type/{cardType}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<BusinessPartnerListResponse?> SearchBusinessPartnersAsync(string searchTerm)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<BusinessPartnerListResponse>($"api/businesspartner/search?q={Uri.EscapeDataString(searchTerm)}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<BusinessPartnerDto?> GetBusinessPartnerByCodeAsync(string cardCode)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<BusinessPartnerDto>($"api/businesspartner/{Uri.EscapeDataString(cardCode)}");
        }
        catch
        {
            return null;
        }
    }
}
