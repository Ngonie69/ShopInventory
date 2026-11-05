using Microsoft.AspNetCore.SignalR;
using ShopInventory.DTOs;
using ShopInventory.Hubs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

// Stand-ins for the hub and push channels so a test can run the real NotificationService and read
// the rows it writes. Only the database row matters to these tests; nothing is ever sent.

internal sealed class NoOpHubContext : IHubContext<NotificationHub>
{
    public IHubClients Clients { get; } = new NoOpClients();
    public IGroupManager Groups { get; } = new NoOpGroupManager();
}

internal sealed class NoOpClients : IHubClients, IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public IClientProxy All => this;
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
    public IClientProxy Client(string connectionId) => this;
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => this;
    public IClientProxy Group(string groupName) => this;
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => this;
    public IClientProxy User(string userId) => this;
    public IClientProxy Users(IReadOnlyList<string> userIds) => this;
}

internal sealed class NoOpGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken ct = default)
        => Task.CompletedTask;
}

internal sealed class StubPushNotificationService : IPushNotificationService
{
    public Task<DeviceRegistrationDto> RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request, CancellationToken ct = default)
        => Task.FromResult(new DeviceRegistrationDto());

    public Task UnregisterDeviceAsync(Guid userId, string deviceToken, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<List<DeviceRegistrationDto>> GetUserDevicesAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult(new List<DeviceRegistrationDto>());

    public Task<int> SendToUserAsync(Guid userId, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<int> SendToUsernameAsync(string username, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<int> SendToRoleAsync(string role, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<int> SendToAllAsync(string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<int> SendSilentDataToUsersAsync(IReadOnlyCollection<Guid> userIds, Dictionary<string, string> data, CancellationToken ct = default) => Task.FromResult(0);

    public Task<int> SendSilentDataToRoleAsync(string role, Dictionary<string, string> data, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<int> SendToDeviceTokensAsync(
        IReadOnlyCollection<string> deviceTokens,
        string title,
        string body,
        Dictionary<string, string>? data = null,
        CancellationToken ct = default)
        => Task.FromResult(0);

    public Task CleanupStaleTokensAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}
