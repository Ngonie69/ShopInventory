using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Webhooks.Queries.GetWebhooks;
using ShopInventory.Features.Webhooks.Queries.GetWebhook;
using ShopInventory.Features.Webhooks.Queries.GetDeliveries;
using ShopInventory.Features.Webhooks.Queries.GetEventTypes;
using ShopInventory.Features.Webhooks.Commands.CreateWebhook;
using ShopInventory.Features.Webhooks.Commands.UpdateWebhook;
using ShopInventory.Features.Webhooks.Commands.DeleteWebhook;
using ShopInventory.Features.Webhooks.Commands.TestWebhook;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "AdminOnly")]
public class WebhookController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetWebhooks(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetWebhooksQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetWebhook(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetWebhookQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost]
    public async Task<IActionResult> CreateWebhook([FromBody] CreateWebhookRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateWebhookCommand(request), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetWebhook), new { id = value.Id }, value),
            errors => Problem(errors));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateWebhook(int id, [FromBody] UpdateWebhookRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateWebhookCommand(id, request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteWebhook(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteWebhookCommand(id), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    [HttpPost("{id}/test")]
    public async Task<IActionResult> TestWebhook(int id, [FromBody] TestWebhookRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new TestWebhookCommand(id, request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("deliveries")]
    public async Task<IActionResult> GetDeliveries(
        [FromQuery] int? webhookId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetDeliveriesQuery(webhookId, page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("event-types")]
    [AllowAnonymous]
    public async Task<IActionResult> GetEventTypes(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetEventTypesQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
