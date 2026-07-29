using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Notification sink for tests. The approval engine publishes as a side effect of recording a
/// decision; nothing under test asserts on those, and a real sink would need a transport.
/// </summary>
internal sealed class NoOpNotificationService : INotificationService
{
    public Task<NotificationDto> CreateNotificationAsync(CreateNotificationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new NotificationDto());

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
