using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Email.Commands.ProcessEmailQueue;
using ShopInventory.Features.Email.Commands.QueueEmail;
using ShopInventory.Features.Email.Commands.SendEmail;
using ShopInventory.Features.Email.Commands.SendTestEmail;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for email operations
/// </summary>
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class EmailController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Send a test email
    /// </summary>
    [HttpPost("test")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(EmailSentResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SendTestEmail([FromBody] TestEmailRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new SendTestEmailCommand(request.ToEmail), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Send an email
    /// </summary>
    [HttpPost("send")]
    [ProducesResponseType(typeof(EmailSentResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SendEmail([FromBody] SendEmailRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new SendEmailCommand(request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Queue an email for later sending
    /// </summary>
    [HttpPost("queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> QueueEmail([FromBody] SendEmailRequest request, [FromQuery] string? category, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new QueueEmailCommand(request, category), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Email queued successfully" }), errors => Problem(errors));
    }

    /// <summary>
    /// Process email queue
    /// </summary>
    [HttpPost("process-queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProcessEmailQueue(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ProcessEmailQueueCommand(), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Email queue processing initiated" }), errors => Problem(errors));
    }
}
