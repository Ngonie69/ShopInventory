using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.PushNotifications.Commands.RegisterDevice;
using ShopInventory.Features.PushNotifications.Commands.SendPushNotification;
using ShopInventory.Features.PushNotifications.Commands.TestPush;
using ShopInventory.Features.PushNotifications.Commands.UnregisterDevice;
using ShopInventory.Features.PushNotifications.Queries.GetMyDevices;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class PushNotificationController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Register a device for push notifications
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(DeviceRegistrationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await mediator.Send(new RegisterDeviceCommand(request, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Unregister a device
    /// </summary>
    [HttpPost("unregister")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UnregisterDevice([FromBody] UnregisterDeviceRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await mediator.Send(new UnregisterDeviceCommand(request.DeviceToken, userId.Value), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    /// <summary>
    /// Get current user's registered devices
    /// </summary>
    [HttpGet("devices")]
    [ProducesResponseType(typeof(List<DeviceRegistrationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyDevices(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await mediator.Send(new GetMyDevicesQuery(userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Send a push notification (Admin only)
    /// </summary>
    [HttpPost("send")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> SendPushNotification([FromBody] SendPushNotificationRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new SendPushNotificationCommand(request), cancellationToken);
        return result.Match(value => Ok(new { sent = value.Sent, title = value.Title }), errors => Problem(errors));
    }

    /// <summary>
    /// Test push notification to the current user's devices
    /// </summary>
    [HttpPost("test")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestPush(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized();

        var result = await mediator.Send(new TestPushCommand(userId.Value), cancellationToken);
        return result.Match(value => Ok(new { sent = value.Sent, message = value.Message }), errors => Problem(errors));
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
