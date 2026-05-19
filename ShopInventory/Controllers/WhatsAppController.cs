using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Net;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.WhatsApp.Commands.ReceiveOpenWAWebhook;
using ShopInventory.Features.WhatsApp.Queries.GetWhatsAppHealth;
using ShopInventory.Features.WhatsApp.Queries.GetWhatsAppInbox;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

[Route("api/whatsapp")]
[Authorize(Policy = "AdminOnly")]
public class WhatsAppController(
    IMediator mediator,
    IOpenWAClient openWaClient,
    IOptions<OpenWASettings> settings,
    ILogger<WhatsAppController> logger) : ApiControllerBase
{
    private readonly IMediator _mediator = mediator;
    private readonly IOpenWAClient _openWaClient = openWaClient;
    private readonly OpenWASettings _settings = settings.Value;
    private readonly ILogger<WhatsAppController> _logger = logger;

    [HttpGet("health")]
    [ProducesResponseType(typeof(WhatsAppHealthDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHealth(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetWhatsAppHealthQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("messages")]
    [ProducesResponseType(typeof(WhatsAppInboxResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMessages(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _mediator.Send(new GetWhatsAppInboxQuery(page, pageSize, search), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("sessions")]
    [ProducesResponseType(typeof(List<WhatsAppSessionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSessions(CancellationToken cancellationToken)
    {
        var configurationErrors = ValidateOperatorAccess();
        if (configurationErrors is not null)
        {
            return Problem(configurationErrors);
        }

        try
        {
            var sessions = await _openWaClient.GetSessionsAsync(cancellationToken);
            return Ok(sessions);
        }
        catch (Exception ex)
        {
            return BuildGatewayFailure(ex, "retrieve WhatsApp sessions");
        }
    }

    [HttpPost("sessions")]
    [ProducesResponseType(typeof(WhatsAppSessionDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateSession([FromBody] WhatsAppCreateSessionRequestDto request, CancellationToken cancellationToken)
    {
        var configurationErrors = ValidateOperatorAccess();
        if (configurationErrors is not null)
        {
            return Problem(configurationErrors);
        }

        try
        {
            var session = await _openWaClient.CreateSessionAsync(request, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, session);
        }
        catch (Exception ex)
        {
            return BuildGatewayFailure(ex, $"create WhatsApp session {request.Name}");
        }
    }

    [HttpPost("sessions/{sessionId}/start")]
    [ProducesResponseType(typeof(WhatsAppSessionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> StartSession(string sessionId, CancellationToken cancellationToken)
    {
        var configurationErrors = ValidateOperatorAccess();
        if (configurationErrors is not null)
        {
            return Problem(configurationErrors);
        }

        try
        {
            var session = await _openWaClient.StartSessionAsync(sessionId, cancellationToken);
            return Ok(session);
        }
        catch (Exception ex)
        {
            return BuildGatewayFailure(ex, $"start WhatsApp session {sessionId}");
        }
    }

    [HttpPost("sessions/{sessionId}/stop")]
    [ProducesResponseType(typeof(WhatsAppSessionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> StopSession(string sessionId, CancellationToken cancellationToken)
    {
        var configurationErrors = ValidateOperatorAccess();
        if (configurationErrors is not null)
        {
            return Problem(configurationErrors);
        }

        try
        {
            var session = await _openWaClient.StopSessionAsync(sessionId, cancellationToken);
            return Ok(session);
        }
        catch (Exception ex)
        {
            return BuildGatewayFailure(ex, $"stop WhatsApp session {sessionId}");
        }
    }

    [HttpGet("sessions/{sessionId}/qr")]
    [ProducesResponseType(typeof(WhatsAppQrCodeDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSessionQrCode(string sessionId, CancellationToken cancellationToken)
    {
        var configurationErrors = ValidateOperatorAccess();
        if (configurationErrors is not null)
        {
            return Problem(configurationErrors);
        }

        try
        {
            var qrCode = await _openWaClient.GetSessionQrCodeAsync(sessionId, cancellationToken);
            return Ok(qrCode);
        }
        catch (Exception ex)
        {
            return BuildGatewayFailure(ex, $"retrieve the QR code for session {sessionId}");
        }
    }

    [HttpPost("sessions/{sessionId}/messages/send-text")]
    [ProducesResponseType(typeof(WhatsAppMessageDispatchDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SendText(string sessionId, [FromBody] WhatsAppSendTextRequestDto request, CancellationToken cancellationToken)
    {
        var configurationErrors = ValidateOperatorAccess();
        if (configurationErrors is not null)
        {
            return Problem(configurationErrors);
        }

        try
        {
            var response = await _openWaClient.SendTextAsync(sessionId, request, cancellationToken);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return BuildGatewayFailure(ex, $"send a WhatsApp message through session {sessionId}");
        }
    }

    [HttpPost("sessions/{sessionId}/messages/reply")]
    [ProducesResponseType(typeof(WhatsAppMessageDispatchDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reply(string sessionId, [FromBody] WhatsAppReplyRequestDto request, CancellationToken cancellationToken)
    {
        var configurationErrors = ValidateOperatorAccess();
        if (configurationErrors is not null)
        {
            return Problem(configurationErrors);
        }

        try
        {
            var response = await _openWaClient.ReplyAsync(sessionId, request, cancellationToken);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return BuildGatewayFailure(ex, $"reply through WhatsApp session {sessionId}");
        }
    }

    [HttpPost("webhook/openwa")]
    [AllowAnonymous]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(WhatsAppWebhookReceiptDto), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ReceiveOpenWaWebhook(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var rawPayload = await reader.ReadToEndAsync(cancellationToken);

        var providedSignature = Request.Headers["X-OpenWA-Signature"].FirstOrDefault()
            ?? Request.Headers["X-Webhook-Signature"].FirstOrDefault();

        var providedEventType = Request.Headers["X-Webhook-Event"].FirstOrDefault()
            ?? Request.Headers["X-OpenWA-Event"].FirstOrDefault()
            ?? Request.Query["event"].FirstOrDefault();

        var idempotencyKey = Request.Headers["X-OpenWA-Idempotency-Key"].FirstOrDefault();
        var deliveryId = Request.Headers["X-OpenWA-Delivery-Id"].FirstOrDefault();

        var result = await _mediator.Send(
            new ReceiveOpenWAWebhookCommand(rawPayload, providedSignature, providedEventType, idempotencyKey, deliveryId, Request.Path.Value),
            cancellationToken);

        return result.Match(value => Accepted(value), errors => Problem(errors));
    }

    private List<Error>? ValidateOperatorAccess()
    {
        var errors = new List<Error>();

        if (!_settings.Enabled)
        {
            errors.Add(Errors.WhatsApp.Disabled);
        }

        if (string.IsNullOrWhiteSpace(_settings.BaseUrl) || _settings.BaseUrl.StartsWith("${", StringComparison.Ordinal))
        {
            errors.Add(Errors.WhatsApp.InvalidConfiguration("OpenWA:BaseUrl must be configured to a valid absolute URL."));
        }

        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || _settings.ApiKey.StartsWith("${", StringComparison.Ordinal))
        {
            errors.Add(Errors.WhatsApp.InvalidConfiguration("OpenWA:ApiKey must be configured before using session and messaging controls."));
        }

        return errors.Count == 0 ? null : errors;
    }

    private IActionResult BuildGatewayFailure(Exception ex, string operation)
    {
        _logger.LogError(ex, "Failed to {Operation}", operation);

        return ex switch
        {
            OpenWAGatewayException gatewayException => Problem(
                statusCode: MapGatewayStatusCode(gatewayException.StatusCode),
                title: gatewayException.Message),
            HttpRequestException httpRequestException => Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: string.IsNullOrWhiteSpace(httpRequestException.Message)
                    ? "OpenWA is unreachable. Start the gateway and try again."
                    : httpRequestException.Message),
            _ => Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: ex.Message)
        };
    }

    private static int MapGatewayStatusCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => StatusCodes.Status400BadRequest,
            HttpStatusCode.NotFound => StatusCodes.Status404NotFound,
            HttpStatusCode.Conflict => StatusCodes.Status409Conflict,
            HttpStatusCode.Unauthorized => StatusCodes.Status503ServiceUnavailable,
            HttpStatusCode.Forbidden => StatusCodes.Status503ServiceUnavailable,
            _ when (int)statusCode >= 500 => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status502BadGateway
        };
    }
}