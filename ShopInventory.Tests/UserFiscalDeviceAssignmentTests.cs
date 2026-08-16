using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.UserManagement.Commands.CreateUser;
using ShopInventory.Features.UserManagement.Commands.UpdateUser;
using ShopInventory.Models;
using ShopInventory.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// Registering a van's handset as a ZIMRA fiscal device from the user screen.
///
/// The id itself is only half of it. A device is one hash-chained receipt sequence with exactly one
/// writer, so two accounts carrying the same id would each sign a different receipt as number N and
/// ZIMRA refuses the whole fiscal day when the file goes up — hours later, with every customer already
/// served and holding a printed receipt. Nothing downstream can detect it, and the field was set by
/// hand in SQL until now, so the guard is asserted here rather than trusted.
/// </summary>
public sealed class UserFiscalDeviceAssignmentTests : IDisposable
{
    private const int DeviceId = 35410;
    private const string Depot = "KEFGRC";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly Guid _adminId = Guid.NewGuid();

    public UserFiscalDeviceAssignmentTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);

        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = _adminId,
            Username = "office",
            Email = "office@example.test",
            PasswordHash = "x",
            Role = ApplicationRoles.Admin,
            IsActive = true
        });

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The update writes an explicit column list, so a field can be accepted, validated and logged and
    /// still never reach the row. Reading it back is the only thing that proves otherwise.
    /// </summary>
    [Fact]
    public async Task A_device_id_is_written_to_the_van()
    {
        var van = await SeedVanAsync("VAN003", deviceId: null);

        var result = await UpdateAsync(van.Id, VanRequest(DeviceId));

        Assert.False(result.IsError);
        Assert.Equal(DeviceId, await ReadDeviceAsync(van.Id));
    }

    [Fact]
    public async Task A_device_already_registered_to_another_van_is_refused()
    {
        await SeedVanAsync("VAN003", DeviceId);
        var second = await SeedVanAsync("VAN005", deviceId: null);

        var result = await UpdateAsync(second.Id, VanRequest(DeviceId));

        Assert.True(result.IsError);
        Assert.Contains("VAN003", result.FirstError.Description);
        Assert.Contains("refuses the whole fiscal day", result.FirstError.Description);
        Assert.Null(await ReadDeviceAsync(second.Id));
    }

    /// <summary>
    /// The holder is not its own conflict. Every other edit on the van — a route, a depot, a name — posts
    /// the device id back unchanged, and refusing that would make the account uneditable.
    /// </summary>
    [Fact]
    public async Task Re_saving_a_van_keeps_the_device_it_already_holds()
    {
        var van = await SeedVanAsync("VAN003", DeviceId);

        var result = await UpdateAsync(van.Id, VanRequest(DeviceId));

        Assert.False(result.IsError);
        Assert.Equal(DeviceId, await ReadDeviceAsync(van.Id));
    }

    /// <summary>
    /// A dormant account is still registered with ZIMRA. Letting a second van take the id while the first
    /// can be switched back on at any time is the same fork, only delayed.
    /// </summary>
    [Fact]
    public async Task A_deactivated_account_still_holds_its_device()
    {
        await SeedVanAsync("VAN003", DeviceId, isActive: false);
        var second = await SeedVanAsync("VAN005", deviceId: null);

        var result = await UpdateAsync(second.Id, VanRequest(DeviceId));

        Assert.True(result.IsError);
        Assert.Contains("VAN003", result.FirstError.Description);
    }

    [Fact]
    public async Task Clearing_the_field_releases_the_device()
    {
        var van = await SeedVanAsync("VAN003", DeviceId);

        var result = await UpdateAsync(van.Id, VanRequest(fiscalDeviceId: null));

        Assert.False(result.IsError);
        Assert.Null(await ReadDeviceAsync(van.Id));
    }

    /// <summary>
    /// Zero is what a blanked number field can post, and the lease handler reads it as "unregistered" —
    /// so storing it would look set on the screen and refuse the van on the road.
    /// </summary>
    [Fact]
    public async Task A_zero_is_not_a_device()
    {
        var van = await SeedVanAsync("VAN003", DeviceId);

        var result = await UpdateAsync(van.Id, VanRequest(fiscalDeviceId: 0));

        Assert.False(result.IsError);
        Assert.Null(await ReadDeviceAsync(van.Id));
    }

    [Fact]
    public async Task Moving_a_van_off_the_role_gives_up_its_device()
    {
        var van = await SeedVanAsync("VAN003", DeviceId);

        var result = await UpdateAsync(van.Id, VanRequest(DeviceId, role: ApplicationRoles.Cashier));

        Assert.False(result.IsError);
        Assert.Null(await ReadDeviceAsync(van.Id));
    }

    [Fact]
    public async Task A_new_van_cannot_be_created_onto_a_registered_device()
    {
        await SeedVanAsync("VAN003", DeviceId);

        var result = await CreateAsync(DeviceId);

        Assert.True(result.IsError);
        Assert.Contains("VAN003", result.FirstError.Description);
        Assert.Equal(1, await _context.Users.CountAsync(user => user.FiscalDeviceId == DeviceId));
    }

    [Fact]
    public async Task A_new_van_takes_a_free_device()
    {
        var result = await CreateAsync(DeviceId);

        Assert.False(result.IsError);
        Assert.Equal(DeviceId, result.Value.FiscalDeviceId);
    }

    private static UpdateUserDetailRequest VanRequest(int? fiscalDeviceId, string role = ApplicationRoles.Sales) => new()
    {
        Role = role,
        AssignedBusinessPartnerCode = "C00001",
        AssignedCostCentreCode = "CC01",
        SupplyingWarehouseCode = Depot,
        FiscalDeviceId = fiscalDeviceId
    };

    private Task<ErrorOr.ErrorOr<ErrorOr.Success>> UpdateAsync(Guid userId, UpdateUserDetailRequest request)
    {
        var handler = new UpdateUserHandler(
            _context,
            OfficeContext(),
            new MemoryCache(new MemoryCacheOptions()),
            new NoOpAuditService(),
            StubProxy.Unused<IBusinessPartnerService>(),
            StubProxy.Unused<INotificationService>(),
            NullLogger<UpdateUserHandler>.Instance);

        return handler.Handle(new UpdateUserCommand(userId, request), CancellationToken.None);
    }

    private Task<ErrorOr.ErrorOr<UserDetailDto>> CreateAsync(int? fiscalDeviceId)
    {
        var handler = new CreateUserHandler(
            _context,
            OfficeContext(),
            new NoOpAuditService(),
            NullLogger<CreateUserHandler>.Instance);

        return handler.Handle(
            new CreateUserCommand(new CreateUserDetailRequest
            {
                Username = $"van-{Guid.NewGuid():N}",
                Email = $"{Guid.NewGuid():N}@example.test",
                Password = "not-a-real-password",
                Role = ApplicationRoles.Sales,
                AssignedWarehouseCodes = ["VAN009"],
                AssignedBusinessPartnerCode = "C00001",
                AssignedCostCentreCode = "CC01",
                SupplyingWarehouseCode = Depot,
                FiscalDeviceId = fiscalDeviceId
            }),
            CancellationToken.None);
    }

    /// <summary>The row as it now stands, read past the change tracker rather than from it.</summary>
    private async Task<int?> ReadDeviceAsync(Guid userId) =>
        await _context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.FiscalDeviceId)
            .SingleAsync();

    private IHttpContextAccessor OfficeContext() =>
        new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.Name, "office"),
                        new Claim(ClaimTypes.NameIdentifier, _adminId.ToString())
                    ],
                    "Test"))
            }
        };

    private async Task<User> SeedVanAsync(string warehouse, int? deviceId = DeviceId, bool isActive = true)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"{warehouse}-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "x",
            Role = ApplicationRoles.Sales,
            FirstName = "Test",
            LastName = "Sales",
            IsActive = isActive,
            AssignedBusinessPartnerCode = "C00001",
            AssignedCostCentreCode = "CC01",
            SupplyingWarehouseCode = Depot,
            FiscalDeviceId = deviceId
        };

        user.AssignedWarehouseCode = warehouse;

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return user;
    }

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) => Task.CompletedTask;
    }
}
