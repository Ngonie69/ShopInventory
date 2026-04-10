using Microsoft.AspNetCore.SignalR.Client;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Manages a persistent SignalR connection to the API's NotificationHub
/// for real-time push notifications.
/// </summary>
public interface INotificationHubService : IAsyncDisposable
{
    event Action<NotificationModel>? OnNotificationReceived;
    Task StartAsync(string accessToken);
    Task StopAsync();
    bool IsConnected { get; }
}

public class NotificationHubService : INotificationHubService, IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotificationHubService> _logger;
    private HubConnection? _hubConnection;

    public event Action<NotificationModel>? OnNotificationReceived;
    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    public NotificationHubService(IConfiguration configuration, ILogger<NotificationHubService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(string accessToken)
    {
        if (_hubConnection is not null)
        {
            if (_hubConnection.State == HubConnectionState.Connected)
                return;
            await DisposeHubAsync();
        }

        var apiBaseUrl = _configuration["ApiSettings:BaseUrl"]?.TrimEnd('/') ?? "http://localhost:5106";
        var hubUrl = $"{apiBaseUrl}/hubs/notifications";

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) })
            .Build();

        _hubConnection.On<NotificationModel>("ReceiveNotification", notification =>
        {
            _logger.LogInformation("Received real-time notification: {Title}", notification.Title);
            OnNotificationReceived?.Invoke(notification);
        });

        _hubConnection.Reconnecting += ex =>
        {
            _logger.LogWarning(ex, "NotificationHub reconnecting...");
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += connectionId =>
        {
            _logger.LogInformation("NotificationHub reconnected: {ConnectionId}", connectionId);
            return Task.CompletedTask;
        };

        _hubConnection.Closed += ex =>
        {
            _logger.LogWarning(ex, "NotificationHub connection closed");
            return Task.CompletedTask;
        };

        try
        {
            await _hubConnection.StartAsync();
            _logger.LogInformation("Connected to NotificationHub at {Url}", hubUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to NotificationHub at {Url}. Will retry on next auth check.", hubUrl);
        }
    }

    public async Task StopAsync()
    {
        await DisposeHubAsync();
    }

    private async Task DisposeHubAsync()
    {
        if (_hubConnection is not null)
        {
            try
            {
                await _hubConnection.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing NotificationHub connection");
            }
            _hubConnection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeHubAsync();
    }
}
