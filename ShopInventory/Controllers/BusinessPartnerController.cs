using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class BusinessPartnerController : ControllerBase
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly SAPSettings _settings;
    private readonly ILogger<BusinessPartnerController> _logger;

    public BusinessPartnerController(
        ISAPServiceLayerClient sapClient,
        IOptions<SAPSettings> settings,
        ILogger<BusinessPartnerController> logger)
    {
        _sapClient = sapClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Gets all business partners (customers) from SAP
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(BusinessPartnerListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetBusinessPartners(CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var partners = await _sapClient.GetBusinessPartnersAsync(cancellationToken);

            return Ok(new BusinessPartnerListResponseDto
            {
                TotalCount = partners.Count,
                BusinessPartners = partners
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving business partners");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error retrieving business partners: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets business partners by type (cCustomer, cSupplier, cLead)
    /// </summary>
    [HttpGet("type/{cardType}")]
    [ProducesResponseType(typeof(BusinessPartnerListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetBusinessPartnersByType(string cardType, CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var partners = await _sapClient.GetBusinessPartnersByTypeAsync(cardType, cancellationToken);

            return Ok(new BusinessPartnerListResponseDto
            {
                TotalCount = partners.Count,
                BusinessPartners = partners
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving business partners by type {CardType}", cardType);
            return StatusCode(500, new ErrorResponseDto { Message = $"Error retrieving business partners: {ex.Message}" });
        }
    }

    /// <summary>
    /// Search business partners by code or name
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(BusinessPartnerListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> SearchBusinessPartners([FromQuery] string q, CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            if (string.IsNullOrWhiteSpace(q))
            {
                return BadRequest(new ErrorResponseDto { Message = "Search term is required" });
            }

            var partners = await _sapClient.SearchBusinessPartnersAsync(q, cancellationToken);

            return Ok(new BusinessPartnerListResponseDto
            {
                TotalCount = partners.Count,
                BusinessPartners = partners
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching business partners with term {SearchTerm}", q);
            return StatusCode(500, new ErrorResponseDto { Message = $"Error searching business partners: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets a specific business partner by card code
    /// </summary>
    [HttpGet("{cardCode}")]
    [ProducesResponseType(typeof(BusinessPartnerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetBusinessPartnerByCode(string cardCode, CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var partner = await _sapClient.GetBusinessPartnerByCodeAsync(cardCode, cancellationToken);

            if (partner == null)
            {
                return NotFound(new ErrorResponseDto { Message = $"Business partner with code '{cardCode}' not found" });
            }

            return Ok(partner);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving business partner {CardCode}", cardCode);
            return StatusCode(500, new ErrorResponseDto { Message = $"Error retrieving business partner: {ex.Message}" });
        }
    }
}
