using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Maintenance.Commands.SetMobileMaintenance;
using ShopInventory.Features.Maintenance.Queries.GetMobileMaintenanceSettings;
using ShopInventory.Features.Maintenance.Queries.GetMobileMaintenanceStatus;

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
    [ProducesResponseType(typeof(MobileMaintenanceStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMobileMaintenanceStatus(
        [FromHeader(Name = "X-App-Id")] string? appIdHeader,
        [FromQuery] string? appId,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetMobileMaintenanceStatusQuery(appId ?? appIdHeader), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// The stored mobile maintenance lockout
    /// </summary>
    [HttpGet("mobile")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(typeof(MobileMaintenanceSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMobileMaintenanceSettings(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetMobileMaintenanceSettingsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Turn the mobile maintenance lockout on or off
    /// </summary>
    [HttpPut("mobile")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(typeof(SetMobileMaintenanceResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetMobileMaintenance(
        [FromBody] SetMobileMaintenanceRequest request,
        CancellationToken cancellationToken)
    {
        var userName = User.Identity?.Name ?? "Unknown";
        var result = await mediator.Send(
            new SetMobileMaintenanceCommand(request, userName), cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
