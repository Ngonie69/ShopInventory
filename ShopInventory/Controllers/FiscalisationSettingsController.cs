using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.FiscalisationConfiguration.Commands.TestFiscalisationConnection;
using ShopInventory.Features.FiscalisationConfiguration.Commands.UpdateFiscalisationSettings;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalisationSettings;

namespace ShopInventory.Controllers;

[Route("api/fiscalisation-settings")]
[Authorize(Policy = "AdminOnly")]
public class FiscalisationSettingsController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Current fiscalisation settings; the API key comes back masked
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetFiscalisationSettingsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Store a new API key
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] UpdateFiscalisationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var userName = User.Identity?.Name ?? "Unknown";
        var result = await mediator.Send(
            new UpdateFiscalisationSettingsCommand(request, userName), cancellationToken);

        return result.Match(
            value => Ok(new
            {
                message = value.Message,
                connectionTestPassed = value.ConnectionTestPassed,
                apiKeyMasked = value.ApiKeyMasked
            }),
            errors => Problem(errors));
    }

    /// <summary>
    /// Check a key against the platform
    /// </summary>
    [HttpPost("test-connection")]
    public async Task<IActionResult> TestConnection(
        [FromBody] TestFiscalisationConnectionRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new TestFiscalisationConnectionCommand(request), cancellationToken);
        return result.Match(
            value => Ok(new { connected = value.Connected, message = value.Message }),
            errors => Problem(errors));
    }
}
