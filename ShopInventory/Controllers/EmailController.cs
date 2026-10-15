using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Features.Email.Commands.ProcessEmailQueue;
using ShopInventory.Features.Email.Commands.QueueEmail;
using ShopInventory.Features.Email.Commands.SendEmail;
using ShopInventory.Features.Email.Commands.SendTestEmail;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for email operations
/// </summary>
/// <remarks>
/// settings.integrations, which only Admin and the API keys hold. Every action here sends mail from
/// the company's own SMTP identity to whatever address the body names, and under "ApiAccess" alone a
/// Lab or Driver login could do it. Nothing in the Web, the tills, the van app or the POD app calls
/// these routes (checked 2026-10-15); the API's own mail goes through the email service directly.
/// </remarks>
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[RequirePermission(Permission.ManageIntegrations)]
public class EmailController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Send a test email, to check the configured SMTP settings actually work.
    /// </summary>
    /// <remarks>
    /// Authenticated, like the rest of this controller. It used to carry <c>[AllowAnonymous]</c>,
    /// which let anyone who could reach the API make the server send mail from its own SMTP identity
    /// to any address they named — a diagnostic that took the recipient from the request body, with
    /// only the global rate limiter in front of it. Nothing called it that way.
    /// </remarks>
    [HttpPost("test")]
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
