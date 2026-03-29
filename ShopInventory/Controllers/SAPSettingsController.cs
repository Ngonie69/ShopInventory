using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for managing SAP connection settings
/// </summary>
[ApiController]
[Route("api/sap-settings")]
[Authorize(Roles = "Admin")]
[Produces("application/json")]
public class SAPSettingsController : ControllerBase
{
    private readonly IOptionsMonitor<SAPSettings> _sapSettings;
    private readonly IConfiguration _configuration;
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly ILogger<SAPSettingsController> _logger;

    public SAPSettingsController(
        IOptionsMonitor<SAPSettings> sapSettings,
        IConfiguration configuration,
        ISAPServiceLayerClient sapClient,
        ILogger<SAPSettingsController> logger)
    {
        _sapSettings = sapSettings;
        _configuration = configuration;
        _sapClient = sapClient;
        _logger = logger;
    }

    /// <summary>
    /// Get current SAP connection settings (password masked)
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(SAPConnectionSettingsDto), StatusCodes.Status200OK)]
    public IActionResult GetSettings()
    {
        var settings = _sapSettings.CurrentValue;
        var dto = new SAPConnectionSettingsDto
        {
            ServiceLayerUrl = settings.ServiceLayerUrl,
            CompanyDB = settings.CompanyDB,
            UserName = settings.Username,
            IsConfigured = !string.IsNullOrEmpty(settings.ServiceLayerUrl)
                           && !string.IsNullOrEmpty(settings.CompanyDB)
                           && !string.IsNullOrEmpty(settings.Username)
        };
        return Ok(dto);
    }

    /// <summary>
    /// Update SAP connection settings in web.config environment variables
    /// </summary>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSAPSettingsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogWarning("SAP settings update requested by {User}", User.Identity?.Name ?? "Unknown");

            // Update the web.config environment variables
            var webConfigPath = Path.Combine(AppContext.BaseDirectory, "web.config");
            if (!System.IO.File.Exists(webConfigPath))
            {
                return BadRequest(new { message = "web.config not found. Settings can only be updated on IIS deployments." });
            }

            var xml = new System.Xml.XmlDocument();
            xml.Load(webConfigPath);

            var envVarsNode = xml.SelectSingleNode("//aspNetCore/environmentVariables");
            if (envVarsNode == null)
            {
                return BadRequest(new { message = "Could not find environmentVariables section in web.config." });
            }

            SetEnvironmentVariable(envVarsNode, xml, "SAP__ServiceLayerUrl", request.ServiceLayerUrl);
            SetEnvironmentVariable(envVarsNode, xml, "SAP__CompanyDB", request.CompanyDB);
            SetEnvironmentVariable(envVarsNode, xml, "SAP__Username", request.UserName);
            SetEnvironmentVariable(envVarsNode, xml, "SAP__Password", request.Password);

            xml.Save(webConfigPath);

            _logger.LogInformation("SAP settings updated in web.config by {User}", User.Identity?.Name ?? "Unknown");

            // Test connection if requested
            if (request.TestConnection)
            {
                try
                {
                    var connected = await _sapClient.TestConnectionAsync(cancellationToken);
                    return Ok(new
                    {
                        message = connected
                            ? "SAP settings updated and connection test successful. App pool restart may be required for changes to fully take effect."
                            : "SAP settings updated but connection test failed. Verify your credentials.",
                        connectionTestPassed = connected
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SAP connection test failed after settings update");
                    return Ok(new
                    {
                        message = $"SAP settings updated but connection test failed: {ex.Message}. App pool restart may be required.",
                        connectionTestPassed = false
                    });
                }
            }

            return Ok(new
            {
                message = "SAP settings updated successfully. App pool restart may be required for changes to take effect.",
                connectionTestPassed = (bool?)null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update SAP settings");
            return BadRequest(new { message = $"Failed to update SAP settings: {ex.Message}" });
        }
    }

    /// <summary>
    /// Test the current SAP connection
    /// </summary>
    [HttpPost("test-connection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestConnection(CancellationToken cancellationToken)
    {
        try
        {
            var connected = await _sapClient.TestConnectionAsync(cancellationToken);
            return Ok(new
            {
                connected,
                message = connected ? "Connection successful" : "Connection failed"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SAP connection test failed");
            return Ok(new
            {
                connected = false,
                message = $"Connection failed: {ex.Message}"
            });
        }
    }

    private static void SetEnvironmentVariable(System.Xml.XmlNode envVarsNode, System.Xml.XmlDocument xml, string name, string value)
    {
        var existing = envVarsNode.SelectSingleNode($"environmentVariable[@name='{name}']");
        if (existing != null)
        {
            existing.Attributes!["value"]!.Value = value;
        }
        else
        {
            var newNode = xml.CreateElement("environmentVariable");
            newNode.SetAttribute("name", name);
            newNode.SetAttribute("value", value);
            envVarsNode.AppendChild(newNode);
        }
    }
}
