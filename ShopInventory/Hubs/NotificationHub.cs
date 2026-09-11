using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ShopInventory.Common.Security;

namespace ShopInventory.Hubs;

[Authorize(Policy = "ApiAccess")]
public class NotificationHub : Hub
{
    /// <summary>The claim <c>AuthService</c> writes for each warehouse a user is assigned to.</summary>
    private const string WarehouseClaimType = "warehouse";

    /// <summary>
    /// The group that reaches every client signed in against one warehouse.
    /// </summary>
    /// <remarks>
    /// Codes are compared case-insensitively but SignalR group names are not, so both the join here
    /// and every send must go through this one function or a till joins "FARM" and the send goes to
    /// "farm" and nothing arrives — silently, because SignalR does not report an empty group.
    /// </remarks>
    public static string WarehouseGroup(string warehouseCode) => $"warehouse:{NormalizeWarehouse(warehouseCode)}";

    private static string NormalizeWarehouse(string warehouseCode) => warehouseCode.Trim().ToUpperInvariant();

    private readonly ILogger<NotificationHub> _logger;
    private readonly ICallerAccountReader _callerAccounts;

    public NotificationHub(ILogger<NotificationHub> logger, ICallerAccountReader callerAccounts)
    {
        _logger = logger;
        _callerAccounts = callerAccounts;
    }

    public override async Task OnConnectedAsync()
    {
        // The user and role groups follow the account. A client that sends an integration key beside
        // the user's token has the key's identity first on the principal, and would otherwise join the
        // key's name and its Admin role's group instead of the user's own.
        var caller = await _callerAccounts.ReadAsync(Context.User, Context.ConnectionAborted);
        if (caller.IsError)
        {
            _logger.LogWarning("NotificationHub: refused a connection whose account is gone or disabled");
            Context.Abort();
            return;
        }

        var username = caller.Value.IsServiceCaller ? Context.User?.Identity?.Name : caller.Value.Username;
        var roles = caller.Value.IsServiceCaller
            ? Context.User?
                .FindAll(ClaimTypes.Role)
                .Select(claim => claim.Value)
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? []
            : new[] { caller.Value.Role! };

        var warehouses = Context.User?
            .FindAll(WarehouseClaimType)
            .Select(claim => claim.Value)
            .Where(warehouse => !string.IsNullOrWhiteSpace(warehouse))
            .Select(NormalizeWarehouse)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];

        if (!string.IsNullOrEmpty(username))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{username}");

        foreach (var role in roles)
            await Groups.AddToGroupAsync(Context.ConnectionId, $"role:{role}");

        // A till is addressed by the warehouse it sells from, not by who is signed in: the same shop
        // is worked by several people over a day and a cancellation concerns the shop, not whoever
        // happens to be at the keyboard.
        foreach (var warehouse in warehouses)
            await Groups.AddToGroupAsync(Context.ConnectionId, WarehouseGroup(warehouse));

        // Everyone joins the broadcast group
        await Groups.AddToGroupAsync(Context.ConnectionId, "all");

        _logger.LogInformation("NotificationHub: {Username} connected (roles={Roles}, warehouses={Warehouses})",
            username, string.Join(",", roles), string.Join(",", warehouses));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var username = Context.User?.Identity?.Name;
        _logger.LogInformation("NotificationHub: {Username} disconnected", username);
        await base.OnDisconnectedAsync(exception);
    }
}
