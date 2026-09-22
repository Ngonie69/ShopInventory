using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Maintenance.Commands.SetMaintenance;
using ShopInventory.Features.Maintenance.Queries.GetMaintenanceSettings;
using ShopInventory.Features.Maintenance.Queries.GetMaintenanceStatus;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
public class MaintenanceController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Whether the calling app is locked out, and what it should tell its user
    /// </summary>
    /// <remarks>
    /// Anonymous and exempt from the lockout it reports on, for the same reason the version check
    /// is: an app has to be able to find out that maintenance is running before it has a token, and
    /// a status endpoint that goes down with the thing it reports would be worse than none. Its
    /// answer comes from the in-process snapshot, so it keeps answering while the database is the
    /// thing under maintenance.
    /// </remarks>
    [HttpGet("mobile/status")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(MaintenanceStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMaintenanceStatus(
        [FromHeader(Name = "X-App-Id")] string? appIdHeader,
        [FromQuery] string? appId,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetMaintenanceStatusQuery(appId ?? appIdHeader), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// The stored mobile maintenance lockout
    /// </summary>
    [HttpGet("mobile")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(typeof(MaintenanceSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMaintenanceSettings(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetMaintenanceSettingsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Turn the mobile maintenance lockout on or off
    /// </summary>
    [HttpPut("mobile")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(typeof(SetMaintenanceResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetMaintenance(
        [FromBody] SetMaintenanceRequest request,
        CancellationToken cancellationToken)
    {
        var userName = User.Identity?.Name ?? "Unknown";
        var result = await mediator.Send(
            new SetMaintenanceCommand(request, userName), cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
