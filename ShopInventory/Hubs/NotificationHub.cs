using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ShopInventory.Hubs;

[Authorize(Policy = "ApiAccess")]
public class NotificationHub : Hub
{
    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(ILogger<NotificationHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var username = Context.User?.Identity?.Name;
        var role = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;

        if (!string.IsNullOrEmpty(username))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{username}");

        if (!string.IsNullOrEmpty(role))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"role:{role}");

        // Everyone joins the broadcast group
        await Groups.AddToGroupAsync(Context.ConnectionId, "all");

        _logger.LogInformation("NotificationHub: {Username} connected (role={Role})", username, role);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var username = Context.User?.Identity?.Name;
        _logger.LogInformation("NotificationHub: {Username} disconnected", username);
        await base.OnDisconnectedAsync(exception);
    }
}
