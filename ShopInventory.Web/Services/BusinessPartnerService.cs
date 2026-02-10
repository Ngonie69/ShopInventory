using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface IBusinessPartnerService
{
    Task<BusinessPartnerListResponse?> GetBusinessPartnersAsync();
    Task<BusinessPartnerListResponse?> GetBusinessPartnersByTypeAsync(string cardType);
    Task<BusinessPartnerListResponse?> SearchBusinessPartnersAsync(string searchTerm);
    Task<BusinessPartnerDto?> GetBusinessPartnerByCodeAsync(string cardCode);

    // Cached operations - use local database
    Task<BusinessPartnerListResponse?> GetCachedBusinessPartnersAsync();
    Task<BusinessPartnerListResponse?> GetCachedBusinessPartnersByTypeAsync(string cardType);
    Task<BusinessPartnerListResponse?> SearchCachedBusinessPartnersAsync(string searchTerm);
    Task<BusinessPartnerDto?> GetCachedBusinessPartnerByCodeAsync(string cardCode);
    Task<int> SyncBusinessPartnersAsync();
    Task<DateTime?> GetLastSyncTimeAsync();
}

public class BusinessPartnerService : IBusinessPartnerService
{
    private readonly HttpClient _httpClient;
    private readonly IDbContextFactory<WebAppDbContext> _dbContextFactory;
    private readonly ILogger<BusinessPartnerService> _logger;

    public BusinessPartnerService(
        HttpClient httpClient,
        IDbContextFactory<WebAppDbContext> dbContextFactory,
        ILogger<BusinessPartnerService> logger)
    {
        _httpClient = httpClient;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    #region API Operations (direct calls to backend)

    public async Task<BusinessPartnerListResponse?> GetBusinessPartnersAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<BusinessPartnerListResponse>("api/businesspartner");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching business partners from API");
            return null;
        }
    }

    public async Task<BusinessPartnerListResponse?> GetBusinessPartnersByTypeAsync(string cardType)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<BusinessPartnerListResponse>($"api/businesspartner/type/{cardType}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching business partners by type from API");
            return null;
        }
    }

    public async Task<BusinessPartnerListResponse?> SearchBusinessPartnersAsync(string searchTerm)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<BusinessPartnerListResponse>($"api/businesspartner/search?q={Uri.EscapeDataString(searchTerm)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching business partners from API");
            return null;
        }
    }

    public async Task<BusinessPartnerDto?> GetBusinessPartnerByCodeAsync(string cardCode)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<BusinessPartnerDto>($"api/businesspartner/{Uri.EscapeDataString(cardCode)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching business partner by code from API");
            return null;
        }
    }

    #endregion

    #region Cached Operations (use local database)

    public async Task<BusinessPartnerListResponse?> GetCachedBusinessPartnersAsync()
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var partners = await db.CachedBusinessPartners
                .Where(p => p.IsActive)
                .OrderBy(p => p.CardName)
                .Select(p => new BusinessPartnerDto
                {
                    CardCode = p.CardCode,
                    CardName = p.CardName,
                    CardType = p.CardType,
                    GroupCode = p.GroupCode,
                    Phone1 = p.Phone1,
                    Phone2 = p.Phone2,
                    Email = p.Email,
                    Address = p.Address,
                    City = p.City,
                    Country = p.Country,
                    Currency = p.Currency,
                    Balance = p.Balance,
                    IsActive = p.IsActive
                })
                .ToListAsync();

            return new BusinessPartnerListResponse
            {
                TotalCount = partners.Count,
                BusinessPartners = partners
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching cached business partners");
            return null;
        }
    }

    public async Task<BusinessPartnerListResponse?> GetCachedBusinessPartnersByTypeAsync(string cardType)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var partners = await db.CachedBusinessPartners
                .Where(p => p.IsActive && p.CardType == cardType)
                .OrderBy(p => p.CardName)
                .Select(p => new BusinessPartnerDto
                {
                    CardCode = p.CardCode,
                    CardName = p.CardName,
                    CardType = p.CardType,
                    GroupCode = p.GroupCode,
                    Phone1 = p.Phone1,
                    Phone2 = p.Phone2,
                    Email = p.Email,
                    Address = p.Address,
                    City = p.City,
                    Country = p.Country,
                    Currency = p.Currency,
                    Balance = p.Balance,
                    IsActive = p.IsActive
                })
                .ToListAsync();

            return new BusinessPartnerListResponse
            {
                TotalCount = partners.Count,
                BusinessPartners = partners
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching cached business partners by type");
            return null;
        }
    }

    public async Task<BusinessPartnerListResponse?> SearchCachedBusinessPartnersAsync(string searchTerm)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var lowerSearch = searchTerm.ToLower();

            var partners = await db.CachedBusinessPartners
                .Where(p => p.IsActive &&
                    (p.CardCode!.ToLower().Contains(lowerSearch) ||
                     p.CardName!.ToLower().Contains(lowerSearch) ||
                     (p.Email != null && p.Email.ToLower().Contains(lowerSearch)) ||
                     (p.Phone1 != null && p.Phone1.Contains(searchTerm))))
                .OrderBy(p => p.CardName)
                .Select(p => new BusinessPartnerDto
                {
                    CardCode = p.CardCode,
                    CardName = p.CardName,
                    CardType = p.CardType,
                    GroupCode = p.GroupCode,
                    Phone1 = p.Phone1,
                    Phone2 = p.Phone2,
                    Email = p.Email,
                    Address = p.Address,
                    City = p.City,
                    Country = p.Country,
                    Currency = p.Currency,
                    Balance = p.Balance,
                    IsActive = p.IsActive
                })
                .ToListAsync();

            return new BusinessPartnerListResponse
            {
                TotalCount = partners.Count,
                BusinessPartners = partners
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching cached business partners");
            return null;
        }
    }

    public async Task<BusinessPartnerDto?> GetCachedBusinessPartnerByCodeAsync(string cardCode)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var partner = await db.CachedBusinessPartners
                .Where(p => p.CardCode == cardCode)
                .Select(p => new BusinessPartnerDto
                {
                    CardCode = p.CardCode,
                    CardName = p.CardName,
                    CardType = p.CardType,
                    GroupCode = p.GroupCode,
                    Phone1 = p.Phone1,
                    Phone2 = p.Phone2,
                    Email = p.Email,
                    Address = p.Address,
                    City = p.City,
                    Country = p.Country,
                    Currency = p.Currency,
                    Balance = p.Balance,
                    IsActive = p.IsActive
                })
                .FirstOrDefaultAsync();

            return partner;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching cached business partner by code");
            return null;
        }
    }

    public async Task<int> SyncBusinessPartnersAsync()
    {
        try
        {
            _logger.LogInformation("Syncing business partners from API...");

            var response = await _httpClient.GetFromJsonAsync<BusinessPartnerListResponse>("api/businesspartner");
            var apiPartners = response?.BusinessPartners ?? new List<BusinessPartnerDto>();

            if (apiPartners.Count == 0)
            {
                _logger.LogWarning("No business partners received from API");
                return 0;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var syncTime = DateTime.UtcNow;
            var updatedCount = 0;
            var insertedCount = 0;

            var existingCardCodes = await db.CachedBusinessPartners
                .Select(p => p.CardCode)
                .ToHashSetAsync();

            foreach (var partner in apiPartners)
            {
                if (string.IsNullOrEmpty(partner.CardCode)) continue;

                if (existingCardCodes.Contains(partner.CardCode))
                {
                    await db.CachedBusinessPartners
                        .Where(cp => cp.CardCode == partner.CardCode)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(cp => cp.CardName, partner.CardName)
                            .SetProperty(cp => cp.CardType, partner.CardType)
                            .SetProperty(cp => cp.GroupCode, partner.GroupCode)
                            .SetProperty(cp => cp.Phone1, partner.Phone1)
                            .SetProperty(cp => cp.Phone2, partner.Phone2)
                            .SetProperty(cp => cp.Email, partner.Email)
                            .SetProperty(cp => cp.Address, partner.Address)
                            .SetProperty(cp => cp.City, partner.City)
                            .SetProperty(cp => cp.Country, partner.Country)
                            .SetProperty(cp => cp.Currency, partner.Currency)
                            .SetProperty(cp => cp.Balance, partner.Balance)
                            .SetProperty(cp => cp.IsActive, partner.IsActive)
                            .SetProperty(cp => cp.LastSyncedAt, syncTime));
                    updatedCount++;
                }
                else
                {
                    db.CachedBusinessPartners.Add(new CachedBusinessPartner
                    {
                        CardCode = partner.CardCode,
                        CardName = partner.CardName,
                        CardType = partner.CardType,
                        GroupCode = partner.GroupCode,
                        Phone1 = partner.Phone1,
                        Phone2 = partner.Phone2,
                        Email = partner.Email,
                        Address = partner.Address,
                        City = partner.City,
                        Country = partner.Country,
                        Currency = partner.Currency,
                        Balance = partner.Balance,
                        IsActive = partner.IsActive,
                        LastSyncedAt = syncTime
                    });
                    insertedCount++;
                }
            }

            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Business partners sync completed: {Inserted} inserted, {Updated} updated",
                insertedCount, updatedCount);

            return apiPartners.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync business partners from API");
            throw;
        }
    }

    public async Task<DateTime?> GetLastSyncTimeAsync()
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            return await db.CachedBusinessPartners
                .OrderByDescending(p => p.LastSyncedAt)
                .Select(p => (DateTime?)p.LastSyncedAt)
                .FirstOrDefaultAsync();
        }
        catch
        {
            return null;
        }
    }

    #endregion
}
