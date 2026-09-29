using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Hubs;

namespace ShopInventory.Services;

/// <summary>A stored notification waiting to be broadcast, pushed and published.</summary>
public sealed record NotificationFanOutWork(
    NotificationDto Dto,
    CreateNotificationRequest Request,
    int NotificationId,
    DateTime CreatedAt,
    string? NormalizedActionUrl,
    string[] BroadcastAudienceRoles);

/// <summary>Takes a stored notification's fan-out off the request that raised it.</summary>
public interface INotificationFanOutQueue
{
    /// <summary>False when the queue is full or stopping; the caller then fans out itself.</summary>
    bool TryEnqueue(NotificationFanOutWork work);
}

/// <summary>
/// Runs <see cref="NotificationService.FanOutAsync"/> for stored notifications, one at a time, each in
/// its own scope, after the request that raised them has answered.
/// </summary>
/// <remarks>
/// <para>
/// Creating an invoice, a payment or a POD used to wait for the notification's SignalR send, a push
/// through Firebase to every device of each audience role in turn, a token update and the webhook
/// lookup: 0.2 to 1 s on every such request, and as long as Google took when it was slow.
/// </para>
/// <para>
/// In memory, and deliberately so: the notification row is already stored, and every fan-out was
/// best-effort before (a failed push was logged and the request carried on). What is still queued
/// when the process stops is finished within the host's shutdown timeout; the bell and the polling
/// fallback read the stored row either way. Bounded, and never dropping: when it is full the caller
/// fans out inline, as before.
/// </para>
/// </remarks>
public sealed class NotificationFanOutQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationFanOutQueue> logger) : BackgroundService, INotificationFanOutQueue
{
    public const int Capacity = 1000;

    private readonly Channel<NotificationFanOutWork> _queue = Channel.CreateBounded<NotificationFanOutWork>(
        new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    public bool TryEnqueue(NotificationFanOutWork work)
    {
        if (_queue.Writer.TryWrite(work))
        {
            return true;
        }

        logger.LogWarning(
            "The notification fan-out queue is full or stopping; notification {NotificationId} fans out on its request",
            work.NotificationId);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not the stopping token: on shutdown the writer is completed and what is queued still goes
        // out, until the host's own shutdown timeout gives up on it.
        await foreach (var work in _queue.Reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var services = scope.ServiceProvider;
                await NotificationService.FanOutAsync(
                    work,
                    services.GetRequiredService<ApplicationDbContext>(),
                    services.GetRequiredService<IHubContext<NotificationHub>>(),
                    services.GetRequiredService<IPushNotificationService>(),
                    services.GetRequiredService<IWebhookService>(),
                    services.GetRequiredService<ILogger<NotificationService>>(),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                // FanOutAsync catches each fan-out's own failure; this is the scope failing to build.
                logger.LogError(ex, "Fanning out notification {NotificationId} failed", work.NotificationId);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }
}
