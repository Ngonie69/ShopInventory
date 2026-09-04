using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.ExceptionCenter.Commands.AcknowledgeExceptionCenterItem;
using ShopInventory.Features.ExceptionCenter.Commands.AssignExceptionCenterItem;
using ShopInventory.Features.ExceptionCenter.Commands.RetryExceptionCenterBatch;
using ShopInventory.Features.ExceptionCenter.Commands.RetryExceptionCenterItem;
using ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;

namespace ShopInventory.Controllers;

[Route("api/exception-center")]
[Authorize(Policy = "ApiAccess")]
public class ExceptionCenterController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// The exception centre dashboard
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ExceptionCenterDashboardDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDashboard(
        [FromQuery] int limit = 100,
        [FromQuery] string? assignee = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetExceptionCenterQuery(limit, assignee), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Retry many at once
    /// </summary>
    [HttpPost("items/retry-batch")]
    [ProducesResponseType(typeof(ExceptionCenterBatchRetryResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RetryBatch(
        [FromBody] ExceptionCenterBatchRetryRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new RetryExceptionCenterBatchCommand(request?.Items ?? []),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Retry one exception centre item
    /// </summary>
    /// <remarks>
    /// <paramref name="itemKey"/> identifies the item within its source: a decimal id for the
    /// int-keyed sources, a Guid for the approval-gated ones. The int-keyed sources are addressed
    /// by exactly the value they always were, so existing callers need no change.
    /// </remarks>
    [HttpPost("items/{source}/{itemKey}/retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RetryItem(string source, string itemKey, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RetryExceptionCenterItemCommand(source, itemKey), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Retry queued" }), errors => Problem(errors));
    }

    /// <summary>
    /// Mark one exception centre item as seen
    /// </summary>
    [HttpPost("items/{source}/{itemKey}/acknowledge")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeItem(string source, string itemKey, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AcknowledgeExceptionCenterItemCommand(source, itemKey), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Item acknowledged" }), errors => Problem(errors));
    }

    /// <summary>
    /// Take ownership of an exception centre item
    /// </summary>
    [HttpPost("items/{source}/{itemKey}/assign-to-me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignItem(string source, string itemKey, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AssignExceptionCenterItemCommand(source, itemKey), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Item assigned" }), errors => Problem(errors));
    }
}