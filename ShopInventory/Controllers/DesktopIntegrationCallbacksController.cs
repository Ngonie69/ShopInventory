using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using ShopInventory.Features.DesktopIntegration.Commands.ProcessTransferEvent;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Features.DesktopIntegration;

namespace ShopInventory.Controllers;

/// <summary>
/// The integrations' callbacks under api/DesktopIntegration: facts another system reports and this
/// API then trusts.
/// </summary>
/// <remarks>
/// A controller of their own so that "ServiceKeyOnly" is the only policy on them. On
/// <see cref="DesktopIntegrationController"/> they inherited "ApiAccess", which any staff login
/// satisfies, so a Lab or Driver account could post a fiscal transaction or a stock transfer. And
/// an action-level policy cannot narrow a class-level one: ASP.NET Core merges them, schemes and
/// all, so the bearer scheme would still have authenticated and an Admin user's token would pass.
/// The routes are unchanged; only the class they live on moved.
/// </remarks>
[ServiceFilter(typeof(DesktopIntegrationAuditFilter))]
[Route("api/DesktopIntegration")]
[Authorize(Policy = "ServiceKeyOnly")]
public class DesktopIntegrationCallbacksController(IMediator mediator) : ApiControllerBase
{
    private string? GetUserId() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("client_id")?.Value;

    private string? GetUsername() =>
        User.Identity?.Name ?? User.FindFirst(ClaimTypes.Name)?.Value;

    /// <summary>
    /// Sync a fiscal transaction back to the local projection
    /// </summary>
    [HttpPost("fiscal-transactions")]
    public async Task<IActionResult> SyncFiscalTransaction(
        [FromBody] SyncFiscalTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new SyncFiscalTransactionCommand(request, GetUserId(), GetUsername()), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Webhook endpoint for TransferEventListener to notify about stock transfers.
    /// </summary>
    [HttpPost("webhook/transfer-event")]
    public async Task<IActionResult> TransferEventWebhook(
        [FromBody] ProcessTransferEventCommand command,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
