using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.AppVersion;
using ShopInventory.Features.Maintenance;
using ShopInventory.Features.Maintenance.Commands.SetMaintenance;
using ShopInventory.Features.Maintenance.Queries.GetMaintenanceSettings;
using ShopInventory.Features.Maintenance.Queries.GetMaintenanceStatus;

namespace ShopInventory.Controllers;

/// <summary>
/// The maintenance lockout: what it says, and how an operator changes it.
/// </summary>
/// <remarks>
/// Every route here is exempt from the lockout itself — <c>MaintenanceGate</c> allows the whole
/// <c>/api/maintenance</c> prefix — so the switch can always be read and always be turned back off.
/// A lockout that froze the screen that lifts it would have to be cleared with a SQL statement
/// against the database somebody was in the middle of restoring.
/// </remarks>
[Route("api/[controller]")]
public class MaintenanceController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Whether the calling client is locked out, and what it should tell its user
    /// </summary>
    /// <remarks>
    /// Anonymous, for the same reason the version check is: a client has to be able to find out
    /// that maintenance is running before it has a token, and a status endpoint that went down with
    /// the thing it reports would be worse than none. Its answer comes from the in-process
    /// snapshot, so it keeps answering while the database is the thing under maintenance.
    ///
    /// The answer is for the caller, not the system: it is judged by the audience this request
    /// belongs to, so the web portal is told about a web portal lockout and a handset about a
    /// handset one. <c>/api/maintenance/mobile/status</c> is the same endpoint under the name the
    /// shipped Android builds already call.
    /// </remarks>
    [HttpGet("status")]
    [HttpGet("mobile/status")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(MaintenanceStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMaintenanceStatus(
        [FromHeader(Name = "X-App-Id")] string? appIdHeader,
        [FromQuery] string? appId,
        CancellationToken cancellationToken)
    {
        var caller = MaintenanceCaller.FromHeaders(Request.Headers);

        // An app id given outright wins over the one on the header, so a caller can ask about an
        // app other than itself. It only ever narrows the mobile audience, so it cannot turn the
        // portal's answer into a phone's.
        if (MobileVersionPolicyAppCatalog.TryResolvePolicyKey(appId ?? appIdHeader, out var policyKey))
        {
            caller = caller with { PolicyKey = policyKey };
        }

        var result = await mediator.Send(new GetMaintenanceStatusQuery(caller), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// The stored maintenance lockout
    /// </summary>
    [HttpGet("")]
    [HttpGet("mobile")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(typeof(MaintenanceSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMaintenanceSettings(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetMaintenanceSettingsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Turn the maintenance lockout on or off
    /// </summary>
    [HttpPut("")]
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
