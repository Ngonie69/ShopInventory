using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Merchandiser.Commands.UpdateProductStatusGlobal;
using ShopInventory.Features.Merchandiser.Events.ProductCatalogChanged;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Deactivating a product tells every merchandiser's phone to refresh its catalogue. Deactivating
/// one that is already deactivated used to do the same: the update matched the rows whatever they
/// held, so it counted as work done and published a catalogue change regardless. Any bulk operation
/// that swept an already-inactive item up with the rest re-announced it, which is how one item came
/// to sit on a phone five times over as "Item GOU015 was deactivated".
/// </summary>
public sealed class ProductCatalogStatusChangeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingPublisher _publisher = new();
    private readonly UpdateProductStatusGlobalHandler _handler;

    public ProductCatalogStatusChangeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _handler = new UpdateProductStatusGlobalHandler(
            _context,
            _publisher,
            NullLogger<UpdateProductStatusGlobalHandler>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task DeactivatingAProductAnnouncesTheCatalogueChange()
    {
        await GivenProductAsync("GOU015", isActive: true);

        var result = await Deactivate("GOU015");

        Assert.Equal(1, result.Value);
        var published = Assert.Single(_publisher.CatalogChanges);
        Assert.Equal(new[] { "GOU015" }, published.ItemCodes);
        Assert.False(published.IsActive);
    }

    [Fact]
    public async Task DeactivatingAnAlreadyInactiveProductAnnouncesNothing()
    {
        await GivenProductAsync("GOU015", isActive: false);

        var result = await Deactivate("GOU015");

        Assert.Equal(0, result.Value);
        Assert.Empty(_publisher.CatalogChanges);
    }

    [Fact]
    public async Task RepeatingADeactivationAnnouncesItOnlyTheFirstTime()
    {
        await GivenProductAsync("GOU015", isActive: true);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Deactivate("GOU015");
        }

        Assert.Single(_publisher.CatalogChanges);
    }

    /// <summary>
    /// A bulk deactivation carries whatever the operator had selected, some of it already inactive.
    /// What is announced is what changed, not what was asked for.
    /// </summary>
    [Fact]
    public async Task ABulkDeactivationAnnouncesOnlyTheProductsItChanged()
    {
        await GivenProductAsync("GOU015", isActive: false);
        await GivenProductAsync("CHE011", isActive: true);
        await GivenProductAsync("PIC003", isActive: true);

        var result = await Deactivate("GOU015", "CHE011", "PIC003");

        Assert.Equal(2, result.Value);
        var published = Assert.Single(_publisher.CatalogChanges);
        Assert.Equal(new[] { "CHE011", "PIC003" }, published.ItemCodes.Order().ToArray());
    }

    /// <summary>
    /// The same item code exists once per merchandiser. One change is one announcement, however
    /// many rows it touched.
    /// </summary>
    [Fact]
    public async Task AnItemHeldBySeveralMerchandisersIsAnnouncedOnce()
    {
        await GivenProductAsync("GOU015", isActive: true);
        await GivenProductAsync("GOU015", isActive: true);
        await GivenProductAsync("GOU015", isActive: true);

        var result = await Deactivate("GOU015");

        Assert.Equal(3, result.Value);
        var published = Assert.Single(_publisher.CatalogChanges);
        Assert.Equal(new[] { "GOU015" }, published.ItemCodes);
    }

    /// <summary>
    /// A row already sitting at the requested status keeps the stamp of whoever last really changed
    /// it, rather than being credited to the operator whose no-op passed over it.
    /// </summary>
    [Fact]
    public async Task AnUnchangedRowKeepsItsLastUpdatedBy()
    {
        await GivenProductAsync("GOU015", isActive: false, updatedBy: "tchuma");

        await Deactivate("GOU015");

        var product = await _context.MerchandiserProducts.AsNoTracking().SingleAsync();
        Assert.Equal("tchuma", product.UpdatedBy);
    }

    private async Task GivenProductAsync(string itemCode, bool isActive, string? updatedBy = null)
    {
        _context.MerchandiserProducts.Add(new MerchandiserProductEntity
        {
            MerchandiserUserId = await AddMerchandiserAsync(),
            ItemCode = itemCode,
            ItemName = itemCode,
            IsActive = isActive,
            UpdatedBy = updatedBy,
            UpdatedAt = updatedBy is null ? null : DateTime.UtcNow.AddDays(-1)
        });

        await _context.SaveChangesAsync();
    }

    private async Task<Guid> AddMerchandiserAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"merchandiser-{suffix}",
            Email = $"merchandiser-{suffix}@example.test",
            PasswordHash = "x",
            Role = ApplicationRoles.Merchandiser,
            IsActive = true
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user.Id;
    }

    private Task<ErrorOr.ErrorOr<int>> Deactivate(params string[] itemCodes) =>
        _handler.Handle(
            new UpdateProductStatusGlobalCommand(
                new UpdateMerchandiserProductStatusRequest
                {
                    ItemCodes = [.. itemCodes],
                    IsActive = false
                },
                "nmutambirwa"),
            CancellationToken.None);

    /// <summary>
    /// What the change turns into once it is published. A merchandiser is answerable for the orders
    /// they submitted, not for the product master, so this reaches their phone as a payload the app
    /// acts on and nothing they are shown.
    /// </summary>
    [Fact]
    public async Task TheCatalogueChangeGoesOutAsASilentPushAndNotANotification()
    {
        var push = new RecordingPushNotificationService();
        var handler = new ProductCatalogChangedNotificationHandler(
            push,
            NullLogger<ProductCatalogChangedNotificationHandler>.Instance);

        await handler.Handle(
            new ProductCatalogChangedEvent(["GOU015"], IsActive: false, "nmutambirwa", DateTime.UtcNow),
            CancellationToken.None);

        var (role, data) = Assert.Single(push.SilentPushes);
        Assert.Equal(ApplicationRoles.Merchandiser, role);
        Assert.Equal("GOU015", data["itemCodes"]);
        Assert.Equal("false", data["isActive"]);
        Assert.Empty(await _context.Notifications.ToListAsync());
    }

    private sealed class RecordingPushNotificationService : IPushNotificationService
    {
        private readonly List<(string Role, Dictionary<string, string> Data)> _silentPushes = [];

        public IReadOnlyList<(string Role, Dictionary<string, string> Data)> SilentPushes => _silentPushes;

        public Task<int> SendSilentDataToRoleAsync(string role, Dictionary<string, string> data, CancellationToken ct = default)
        {
            _silentPushes.Add((role, data));
            return Task.FromResult(1);
        }

        // A catalogue change has no business waking anyone up.
        public Task<int> SendToRoleAsync(string role, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) =>
            throw new InvalidOperationException($"A catalogue change was pushed to {role} as a visible notification: \"{title}\".");

        public Task<int> SendToUserAsync(Guid userId, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) =>
            throw new InvalidOperationException($"A catalogue change was pushed as a visible notification: \"{title}\".");

        public Task<int> SendToUsernameAsync(string username, string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) =>
            throw new InvalidOperationException($"A catalogue change was pushed as a visible notification: \"{title}\".");

        public Task<int> SendToAllAsync(string title, string body, Dictionary<string, string>? data = null, CancellationToken ct = default) =>
            throw new InvalidOperationException($"A catalogue change was pushed as a visible notification: \"{title}\".");

        public Task<DeviceRegistrationDto> RegisterDeviceAsync(Guid userId, RegisterDeviceRequest request, CancellationToken ct = default) =>
            Task.FromResult(new DeviceRegistrationDto());

        public Task UnregisterDeviceAsync(Guid userId, string deviceToken, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<List<DeviceRegistrationDto>> GetUserDevicesAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult(new List<DeviceRegistrationDto>());

        public Task<int> SendToDeviceTokensAsync(
            IReadOnlyCollection<string> deviceTokens,
            string title,
            string body,
            Dictionary<string, string>? data = null,
            CancellationToken ct = default)
            => Task.FromResult(0);

        public Task CleanupStaleTokensAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingPublisher : IPublisher
    {
        private readonly List<ProductCatalogChangedEvent> _catalogChanges = [];

        public IReadOnlyList<ProductCatalogChangedEvent> CatalogChanges => _catalogChanges;

        public Task Publish(object notification, CancellationToken cancellationToken = default)
        {
            if (notification is ProductCatalogChangedEvent catalogChange)
            {
                _catalogChanges.Add(catalogChange);
            }

            return Task.CompletedTask;
        }

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => Publish((object)notification!, cancellationToken);
    }
}
