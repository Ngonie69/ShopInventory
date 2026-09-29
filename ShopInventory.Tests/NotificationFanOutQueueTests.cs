using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A stored notification's broadcast, push and webhook go out after the save that raised it has
/// answered, in the queue's own scope; without a queue, or with a full one, they go out inline as
/// they always did; and what is queued still goes out when the app stops.
/// </summary>
public sealed class NotificationFanOutQueueTests : IAsyncLifetime
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"fanout-{Guid.NewGuid():N}.db");
    private readonly BlockingPush _push = new();
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        // A file, not :memory:: the queue reads the database from its own scope on another thread.
        var connectionString = $"Data Source={_databasePath};Pooling=False";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IPushNotificationService>(_push.Service);
        services.AddSingleton(StubProxy.Unused<IWebhookService>());
        services.AddSingleton(SilentHub());
        services.AddSingleton<NotificationFanOutQueue>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task A_save_answers_while_the_push_is_still_going_out()
    {
        var queue = _provider.GetRequiredService<NotificationFanOutQueue>();
        await queue.StartAsync(CancellationToken.None);

        using var scope = _provider.CreateScope();
        var created = CreateService(scope, queue).CreateNotificationAsync(RoleNotification("Invoice posted"));

        // Firebase is slow today: the push is held open, and the save must not wait for it.
        var dto = await created.WaitAsync(Soon);
        Assert.Equal("Invoice posted", dto.Title);
        Assert.Empty(_push.Sent);

        _push.Release();
        await WaitUntilAsync(() => _push.Sent.Count == 1);
        Assert.Equal("role:Cashier", Assert.Single(_push.Sent));

        await queue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Without_a_queue_the_save_still_waits_for_the_push_as_before()
    {
        using var scope = _provider.CreateScope();
        var created = CreateService(scope, fanOut: null).CreateNotificationAsync(RoleNotification("Invoice posted"));

        await Task.Delay(300);
        Assert.False(created.IsCompleted);

        _push.Release();
        await created.WaitAsync(Soon);
        Assert.Equal("role:Cashier", Assert.Single(_push.Sent));
    }

    [Fact]
    public async Task A_full_queue_fans_out_on_the_request_rather_than_dropping_it()
    {
        _push.Release();
        var full = new RefusingQueue();

        using var scope = _provider.CreateScope();
        await CreateService(scope, full).CreateNotificationAsync(RoleNotification("Payment received"));

        Assert.Equal(1, full.Refused);
        Assert.Equal("role:Cashier", Assert.Single(_push.Sent));
    }

    [Fact]
    public async Task What_is_queued_still_goes_out_when_the_app_stops()
    {
        var queue = _provider.GetRequiredService<NotificationFanOutQueue>();
        await queue.StartAsync(CancellationToken.None);

        using (var scope = _provider.CreateScope())
        {
            var service = CreateService(scope, queue);
            for (var i = 0; i < 3; i++)
            {
                await service.CreateNotificationAsync(RoleNotification($"POD uploaded {i}")).WaitAsync(Soon);
            }
        }

        var stopping = queue.StopAsync(CancellationToken.None);
        _push.Release();
        await stopping.WaitAsync(Soon);

        Assert.Equal(3, _push.Sent.Count);
    }

    [Fact]
    public async Task A_queued_push_resolves_its_user_from_its_own_scope_after_the_request_has_gone()
    {
        Guid userId;
        using (var seed = _provider.CreateScope())
        {
            var context = seed.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = new User { Username = "tendai", PasswordHash = "x", Role = "Cashier", Email = "t@example.invalid" };
            context.Users.Add(user);
            await context.SaveChangesAsync();
            userId = user.Id;
        }

        var queue = _provider.GetRequiredService<NotificationFanOutQueue>();
        await queue.StartAsync(CancellationToken.None);

        // The request's scope, and its DbContext, are gone before the push goes out.
        using (var request = _provider.CreateScope())
        {
            await CreateService(request, queue).CreateNotificationAsync(new CreateNotificationRequest
            {
                Title = "Your transfer was approved",
                Message = "Transfer 4411",
                Category = "Transfer",
                TargetUsername = "tendai"
            }).WaitAsync(Soon);
        }

        _push.Release();
        await WaitUntilAsync(() => _push.Sent.Count == 1);
        Assert.Equal($"user:{userId}", Assert.Single(_push.Sent));

        await queue.StopAsync(CancellationToken.None);
    }

    private NotificationService CreateService(IServiceScope scope, INotificationFanOutQueue? fanOut) =>
        new(
            scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
            NullLogger<NotificationService>.Instance,
            _provider.GetRequiredService<IHubContext<NotificationHub>>(),
            _push.Service,
            _provider.GetRequiredService<IWebhookService>(),
            fanOut);

    private static CreateNotificationRequest RoleNotification(string title) => new()
    {
        Title = title,
        Message = title,
        Category = "Invoice",
        TargetRole = "Cashier"
    };

    private static IHubContext<NotificationHub> SilentHub()
    {
        var proxy = StubProxy.For<IClientProxy>((_, _) => Task.CompletedTask);
        var clients = StubProxy.For<IHubClients>((_, _) => proxy);
        return StubProxy.For<IHubContext<NotificationHub>>((method, _) => method.Name switch
        {
            "get_Clients" => clients,
            _ => throw new InvalidOperationException(method.Name)
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Soon;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    /// <summary>A push that waits until released, as Firebase does on a slow day, and records who it reached.</summary>
    private sealed class BlockingPush
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> SentQueue { get; } = new();

        public List<string> Sent => [.. SentQueue];

        public void Release() => _release.TrySetResult();

        public IPushNotificationService Service => StubProxy.For<IPushNotificationService>((method, args) => method.Name switch
        {
            nameof(IPushNotificationService.SendToRoleAsync) => Send($"role:{args![0]}", (CancellationToken)args[^1]!),
            nameof(IPushNotificationService.SendToUserAsync) => Send($"user:{args![0]}", (CancellationToken)args[^1]!),
            _ => throw new InvalidOperationException($"Unexpected push: {method.Name}")
        });

        // Honours its token as the real one does, so a push cut off at shutdown shows up as missing.
        private async Task<int> Send(string target, CancellationToken ct)
        {
            await _release.Task.WaitAsync(ct);
            SentQueue.Enqueue(target);
            return 1;
        }
    }

    private sealed class RefusingQueue : INotificationFanOutQueue
    {
        public int Refused { get; private set; }

        public bool TryEnqueue(NotificationFanOutWork work)
        {
            Refused++;
            return false;
        }
    }
}
