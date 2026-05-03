using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.ExceptionCenter.Commands.AcknowledgeExceptionCenterItem;
using ShopInventory.Features.ExceptionCenter.Commands.AssignExceptionCenterItem;
using ShopInventory.Features.ExceptionCenter.Commands.RetryExceptionCenterItem;
using ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;

namespace ShopInventory.Controllers;

[Route("api/exception-center")]
[Authorize(Policy = "ApiAccess")]
public class ExceptionCenterController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ExceptionCenterDashboardDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDashboard([FromQuery] int limit = 100, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetExceptionCenterQuery(limit), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("items/{source}/{itemId:int}/retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RetryItem(string source, int itemId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RetryExceptionCenterItemCommand(source, itemId), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Retry queued" }), errors => Problem(errors));
    }

    [HttpPost("items/{source}/{itemId:int}/acknowledge")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeItem(string source, int itemId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AcknowledgeExceptionCenterItemCommand(source, itemId), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Item acknowledged" }), errors => Problem(errors));
    }

    [HttpPost("items/{source}/{itemId:int}/assign-to-me")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignItem(string source, int itemId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new AssignExceptionCenterItemCommand(source, itemId), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Item assigned" }), errors => Problem(errors));
    }
}