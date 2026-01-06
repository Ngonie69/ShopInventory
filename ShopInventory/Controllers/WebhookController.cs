using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Services;
using Swashbuckle.AspNetCore.Annotations;

namespace ShopInventory.Controllers;

/// <summary>
/// Manages webhook subscriptions for event notifications to external systems
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class WebhookController : ControllerBase
{
    private readonly IWebhookService _webhookService;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(IWebhookService webhookService, ILogger<WebhookController> logger)
    {
        _webhookService = webhookService;
        _logger = logger;
    }

    /// <summary>
    /// Get all webhook subscriptions
    /// </summary>
    [HttpGet]
    [SwaggerOperation(Summary = "Get all webhooks", Description = "Retrieves all webhook subscriptions")]
    [SwaggerResponse(200, "List of webhooks retrieved successfully", typeof(List<WebhookDto>))]
    public async Task<ActionResult<List<WebhookDto>>> GetWebhooks()
    {
        var webhooks = await _webhookService.GetAllWebhooksAsync();
        return Ok(webhooks);
    }

    /// <summary>
    /// Get a specific webhook by ID
    /// </summary>
    /// <param name="id">Webhook ID</param>
    [HttpGet("{id}")]
    [SwaggerOperation(Summary = "Get webhook by ID", Description = "Retrieves a specific webhook subscription")]
    [SwaggerResponse(200, "Webhook retrieved successfully", typeof(WebhookDto))]
    [SwaggerResponse(404, "Webhook not found")]
    public async Task<ActionResult<WebhookDto>> GetWebhook(int id)
    {
        var webhook = await _webhookService.GetWebhookByIdAsync(id);
        if (webhook == null)
        {
            return NotFound(new { message = "Webhook not found" });
        }
        return Ok(webhook);
    }

    /// <summary>
    /// Create a new webhook subscription
    /// </summary>
    /// <param name="request">Webhook creation request</param>
    [HttpPost]
    [SwaggerOperation(Summary = "Create webhook", Description = "Creates a new webhook subscription for event notifications")]
    [SwaggerResponse(201, "Webhook created successfully", typeof(WebhookDto))]
    [SwaggerResponse(400, "Invalid request")]
    public async Task<ActionResult<WebhookDto>> CreateWebhook([FromBody] CreateWebhookRequest request)
    {
        try
        {
            var webhook = await _webhookService.CreateWebhookAsync(request);
            return CreatedAtAction(nameof(GetWebhook), new { id = webhook.Id }, webhook);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Update an existing webhook subscription
    /// </summary>
    /// <param name="id">Webhook ID</param>
    /// <param name="request">Webhook update request</param>
    [HttpPut("{id}")]
    [SwaggerOperation(Summary = "Update webhook", Description = "Updates an existing webhook subscription")]
    [SwaggerResponse(200, "Webhook updated successfully", typeof(WebhookDto))]
    [SwaggerResponse(400, "Invalid request")]
    [SwaggerResponse(404, "Webhook not found")]
    public async Task<ActionResult<WebhookDto>> UpdateWebhook(int id, [FromBody] UpdateWebhookRequest request)
    {
        try
        {
            var webhook = await _webhookService.UpdateWebhookAsync(id, request);
            if (webhook == null)
            {
                return NotFound(new { message = "Webhook not found" });
            }
            return Ok(webhook);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Delete a webhook subscription
    /// </summary>
    /// <param name="id">Webhook ID</param>
    [HttpDelete("{id}")]
    [SwaggerOperation(Summary = "Delete webhook", Description = "Deletes a webhook subscription")]
    [SwaggerResponse(204, "Webhook deleted successfully")]
    [SwaggerResponse(404, "Webhook not found")]
    public async Task<IActionResult> DeleteWebhook(int id)
    {
        var deleted = await _webhookService.DeleteWebhookAsync(id);
        if (!deleted)
        {
            return NotFound(new { message = "Webhook not found" });
        }
        return NoContent();
    }

    /// <summary>
    /// Test a webhook by sending a test payload
    /// </summary>
    /// <param name="id">Webhook ID</param>
    /// <param name="request">Test request with event type and optional sample data</param>
    [HttpPost("{id}/test")]
    [SwaggerOperation(Summary = "Test webhook", Description = "Sends a test payload to the webhook URL")]
    [SwaggerResponse(200, "Test result", typeof(TestWebhookResponse))]
    [SwaggerResponse(404, "Webhook not found")]
    public async Task<ActionResult<TestWebhookResponse>> TestWebhook(int id, [FromBody] TestWebhookRequest request)
    {
        var result = await _webhookService.TestWebhookAsync(id, request);
        if (result.ErrorMessage == "Webhook not found")
        {
            return NotFound(new { message = "Webhook not found" });
        }
        return Ok(result);
    }

    /// <summary>
    /// Get webhook delivery logs
    /// </summary>
    /// <param name="webhookId">Optional webhook ID to filter by</param>
    /// <param name="page">Page number (default: 1)</param>
    /// <param name="pageSize">Page size (default: 50)</param>
    [HttpGet("deliveries")]
    [SwaggerOperation(Summary = "Get delivery logs", Description = "Retrieves webhook delivery history")]
    [SwaggerResponse(200, "Delivery logs retrieved successfully", typeof(WebhookDeliveryListResponse))]
    public async Task<ActionResult<WebhookDeliveryListResponse>> GetDeliveries(
        [FromQuery] int? webhookId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var deliveries = await _webhookService.GetDeliveriesAsync(webhookId, page, pageSize);
        return Ok(deliveries);
    }

    /// <summary>
    /// Get available webhook event types
    /// </summary>
    [HttpGet("event-types")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Get event types", Description = "Retrieves all available webhook event types")]
    [SwaggerResponse(200, "Event types retrieved successfully", typeof(WebhookEventTypesResponse))]
    public async Task<ActionResult<WebhookEventTypesResponse>> GetEventTypes()
    {
        var eventTypes = await _webhookService.GetEventTypesAsync();
        return Ok(new WebhookEventTypesResponse { EventTypes = eventTypes });
    }
}
