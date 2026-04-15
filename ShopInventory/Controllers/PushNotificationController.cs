using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Services;
using System.Security.Claims;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class PushNotificationController : ControllerBase
{
    private readonly IPushNotificationService _pushService;
    private readonly ILogger<PushNotificationController> _logger;

    public PushNotificationController(IPushNotificationService pushService, ILogger<PushNotificationController> logger)
    {
        _pushService = pushService;
        _logger = logger;
    }

    /// <summary>
    /// Register a device for push notifications
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(DeviceRegistrationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await _pushService.RegisterDeviceAsync(userId.Value, request, ct);
        return Ok(result);
    }

    /// <summary>
    /// Unregister a device
    /// </summary>
    [HttpPost("unregister")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UnregisterDevice([FromBody] UnregisterDeviceRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        await _pushService.UnregisterDeviceAsync(userId.Value, request.DeviceToken, ct);
        return NoContent();
    }

    /// <summary>
    /// Get current user's registered devices
    /// </summary>
    [HttpGet("devices")]
    [ProducesResponseType(typeof(List<DeviceRegistrationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyDevices(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var devices = await _pushService.GetUserDevicesAsync(userId.Value, ct);
        return Ok(devices);
    }

    /// <summary>
    /// Send a push notification (Admin only)
    /// </summary>
    [HttpPost("send")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> SendPushNotification([FromBody] SendPushNotificationRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        int sent;
        var data = request.Data ?? new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(request.TargetUsername))
        {
            sent = await _pushService.SendToUsernameAsync(request.TargetUsername, request.Title, request.Body, data, ct);
        }
        else if (!string.IsNullOrEmpty(request.TargetRole))
        {
            sent = await _pushService.SendToRoleAsync(request.TargetRole, request.Title, request.Body, data, ct);
        }
        else
        {
            sent = await _pushService.SendToAllAsync(request.Title, request.Body, data, ct);
        }

        return Ok(new { sent, title = request.Title });
    }

    /// <summary>
    /// Test push notification to the current user's devices
    /// </summary>
    [HttpPost("test")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestPush(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var sent = await _pushService.SendToUserAsync(
            userId.Value,
            "Test Notification",
            "This is a test push notification from ShopInventory.",
            new Dictionary<string, string> { ["type"] = "test" },
            ct);

        return Ok(new { sent, message = "Test push notification sent" });
    }

    private Guid? GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("sub")?.Value;

        if (Guid.TryParse(userIdClaim, out var userId))
            return userId;

        return null;
    }
}
