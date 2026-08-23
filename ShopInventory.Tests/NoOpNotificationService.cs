using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Notification sink for tests. The approval engine publishes as a side effect of recording a
/// decision, and a real sink would need a transport.
/// </summary>
/// <remarks>
/// Everything handed to it is kept in <see cref="Sent"/> rather than dropped, so a test that cares
/// what was published can assert on it and a test that does not can carry on ignoring it. Worth
/// keeping for the ones that publish to a van: a notification that is never sent and a notification
/// sent to nobody look identical from outside, and neither fails anything on its own.
/// </remarks>
internal sealed class NoOpNotificationService : INotificationService
{
    /// <summary>Every request this sink was handed, in the order it was handed them.</summary>
    public List<CreateNotificationRequest> Sent { get; } = [];

    public Task<NotificationDto> CreateNotificationAsync(CreateNotificationRequest request, CancellationToken cancellationToken = default)
    {
        Sent.Add(request);
        return Task.FromResult(new NotificationDto());
    }

    public Task<NotificationListResponseDto> GetNotificationsAsync(string? username, IReadOnlyCollection<string>? roles, int page = 1, int pageSize = 20, bool unreadOnly = false, string? category = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new NotificationListResponseDto());

    public Task<int> GetUnreadCountAsync(string? username, IReadOnlyCollection<string>? roles, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task MarkAsReadAsync(string? username, IReadOnlyCollection<string>? roles, List<int>? notificationIds, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DeleteNotificationAsync(int id, string? username, IReadOnlyCollection<string>? roles, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CleanupExpiredNotificationsAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CreateLowStockAlertAsync(string itemCode, string itemName, decimal currentStock, decimal reorderLevel, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CreateSystemAlertAsync(string title, string message, string type = "Info", CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task CreateSalesOrderNotificationAsync(int orderId, string orderNumber, string customerCode, string customerName, decimal docTotal, string status, string source, string? createdByUsername, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
