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
    event Action? OnDesktopSaleCreated;
    event Action? OnStockSnapshotUpdated;
    event Action<StockFetchProgressModel>? OnStockFetchProgress;
    event Action? OnConsolidationCompleted;
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
    public event Action? OnDesktopSaleCreated;
    public event Action? OnStockSnapshotUpdated;
    public event Action<StockFetchProgressModel>? OnStockFetchProgress;
    public event Action? OnConsolidationCompleted;
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

        _hubConnection.On("DesktopSaleCreated", () =>
        {
            _logger.LogInformation("Real-time: DesktopSaleCreated");
            OnDesktopSaleCreated?.Invoke();
        });

        _hubConnection.On("StockSnapshotUpdated", () =>
        {
            _logger.LogInformation("Real-time: StockSnapshotUpdated");
            OnStockSnapshotUpdated?.Invoke();
        });

        _hubConnection.On<StockFetchProgressModel>("StockFetchProgress", progress =>
        {
            _logger.LogInformation("Real-time: StockFetchProgress {Completed}/{Total} — {Warehouse}",
                progress.CompletedCount, progress.TotalCount, progress.CurrentWarehouse);
            OnStockFetchProgress?.Invoke(progress);
        });

        _hubConnection.On("ConsolidationCompleted", () =>
        {
            _logger.LogInformation("Real-time: ConsolidationCompleted");
            OnConsolidationCompleted?.Invoke();
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
