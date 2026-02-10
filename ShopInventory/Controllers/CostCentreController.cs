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
public class CostCentreController : ControllerBase
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly SAPSettings _settings;
    private readonly ILogger<CostCentreController> _logger;

    public CostCentreController(
        ISAPServiceLayerClient sapClient,
        IOptions<SAPSettings> settings,
        ILogger<CostCentreController> logger)
    {
        _sapClient = sapClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Gets all active cost centres (profit centers) from SAP.
    /// These rarely change and should be cached client-side.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(CostCentreListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCostCentres(CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var costCentres = await _sapClient.GetCostCentresAsync(cancellationToken);

            return Ok(new CostCentreListResponseDto
            {
                TotalCount = costCentres.Count,
                CostCentres = costCentres
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving cost centres");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error retrieving cost centres: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets cost centres for a specific dimension (1-5)
    /// </summary>
    [HttpGet("dimension/{dimension:int}")]
    [ProducesResponseType(typeof(CostCentreListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCostCentresByDimension(int dimension, CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            if (dimension < 1 || dimension > 5)
            {
                return BadRequest(new ErrorResponseDto { Message = "Dimension must be between 1 and 5" });
            }

            var costCentres = await _sapClient.GetCostCentresByDimensionAsync(dimension, cancellationToken);

            return Ok(new CostCentreListResponseDto
            {
                TotalCount = costCentres.Count,
                CostCentres = costCentres
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving cost centres by dimension {Dimension}", dimension);
            return StatusCode(500, new ErrorResponseDto { Message = $"Error retrieving cost centres: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets a specific cost centre by code
    /// </summary>
    [HttpGet("{centerCode}")]
    [ProducesResponseType(typeof(CostCentreDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCostCentreByCode(string centerCode, CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var costCentre = await _sapClient.GetCostCentreByCodeAsync(centerCode, cancellationToken);

            if (costCentre == null)
            {
                return NotFound(new ErrorResponseDto { Message = $"Cost centre with code '{centerCode}' not found" });
            }

            return Ok(costCentre);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving cost centre {CenterCode}", centerCode);
            return StatusCode(500, new ErrorResponseDto { Message = $"Error retrieving cost centre: {ex.Message}" });
        }
    }
}
