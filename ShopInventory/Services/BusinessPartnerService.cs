using ShopInventory.DTOs;

namespace ShopInventory.Services;

public interface IBusinessPartnerService
{
    Task<List<BusinessPartnerDto>> GetBusinessPartnersAsync(CancellationToken cancellationToken = default);
    Task<List<BusinessPartnerDto>> GetBusinessPartnersByTypeAsync(string cardType, CancellationToken cancellationToken = default);
    Task<List<BusinessPartnerDto>> SearchBusinessPartnersAsync(string searchTerm, CancellationToken cancellationToken = default);
    Task<BusinessPartnerDto?> GetBusinessPartnerByCodeAsync(string cardCode, CancellationToken cancellationToken = default);
}

public class BusinessPartnerService : IBusinessPartnerService
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<BusinessPartnerService> _logger;

    public BusinessPartnerService(ISAPServiceLayerClient sapClient, ILogger<BusinessPartnerService> logger)
    {
        _sapClient = sapClient;
        _logger = logger;
    }

    public async Task<List<BusinessPartnerDto>> GetBusinessPartnersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _sapClient.GetBusinessPartnersAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving business partners");
            return new List<BusinessPartnerDto>();
        }
    }

    public async Task<List<BusinessPartnerDto>> GetBusinessPartnersByTypeAsync(string cardType, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _sapClient.GetBusinessPartnersByTypeAsync(cardType, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving business partners by type {CardType}", cardType);
            return new List<BusinessPartnerDto>();
        }
    }

    public async Task<List<BusinessPartnerDto>> SearchBusinessPartnersAsync(string searchTerm, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _sapClient.SearchBusinessPartnersAsync(searchTerm, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching business partners with term {SearchTerm}", searchTerm);
            return new List<BusinessPartnerDto>();
        }
    }

    public async Task<BusinessPartnerDto?> GetBusinessPartnerByCodeAsync(string cardCode, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _sapClient.GetBusinessPartnerByCodeAsync(cardCode, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving business partner by code {CardCode}", cardCode);
            return null;
        }
    }
}
