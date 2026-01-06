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
public class GLAccountController : ControllerBase
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly SAPSettings _settings;
    private readonly ILogger<GLAccountController> _logger;

    public GLAccountController(
        ISAPServiceLayerClient sapClient,
        IOptions<SAPSettings> settings,
        ILogger<GLAccountController> logger)
    {
        _sapClient = sapClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Gets all active G/L accounts from SAP
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(GLAccountListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetGLAccounts(CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var accounts = await _sapClient.GetGLAccountsAsync(cancellationToken);

            return Ok(new GLAccountListResponseDto
            {
                TotalCount = accounts.Count,
                Accounts = accounts
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving G/L accounts");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error retrieving G/L accounts: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets G/L accounts by type (at_Revenues, at_Expenses, at_Other)
    /// </summary>
    [HttpGet("type/{accountType}")]
    [ProducesResponseType(typeof(GLAccountListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetGLAccountsByType(string accountType, CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var accounts = await _sapClient.GetGLAccountsByTypeAsync(accountType, cancellationToken);

            return Ok(new GLAccountListResponseDto
            {
                TotalCount = accounts.Count,
                Accounts = accounts
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving G/L accounts by type {AccountType}", accountType);
            return StatusCode(500, new ErrorResponseDto { Message = $"Error retrieving G/L accounts: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets a specific G/L account by code
    /// </summary>
    [HttpGet("{accountCode}")]
    [ProducesResponseType(typeof(GLAccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetGLAccountByCode(string accountCode, CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var account = await _sapClient.GetGLAccountByCodeAsync(accountCode, cancellationToken);

            if (account == null)
            {
                return NotFound(new ErrorResponseDto { Message = $"G/L account with code '{accountCode}' not found" });
            }

            return Ok(account);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving G/L account {AccountCode}", accountCode);
            return StatusCode(500, new ErrorResponseDto { Message = $"Error retrieving G/L account: {ex.Message}" });
        }
    }
}
