using System.IdentityModel.Tokens.Jwt;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins which warehouses an access token names, because <c>NotificationHub</c> addresses a till's
/// connection by exactly those.
/// </summary>
/// <remarks>
/// The token used to read the account's own warehouse column and nothing else. A till operator takes
/// its warehouse from its shop and never has that column filled, so every till token named no
/// warehouse, every till connection joined no warehouse group, and <c>InvoiceCancelled</c> — sent only
/// to warehouse groups — reached none of the tills it was written for.
/// </remarks>
public sealed class AccessTokenWarehouseClaimTests : IDisposable
{
    private const string Ip = "10.10.11.27";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public AccessTokenWarehouseClaimTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_till_operators_token_names_its_shops_warehouse()
    {
        var shop = new ShopEntity { Code = "GRC", Name = "Groombridge", WarehouseCode = "GRC-FAC", BusinessPartnerCode = "CIS004" };
        _context.Shops.Add(shop);
        await _context.SaveChangesAsync();

        var user = AddUser(ApplicationRoles.TillOperator, shopId: shop.Id);

        Assert.Equal(["GRC-FAC"], await WarehouseClaimsAsync(user));
    }

    [Fact]
    public async Task An_account_without_a_shop_keeps_its_own_warehouses()
    {
        var user = AddUser(ApplicationRoles.StockController, warehouses: ["FARM", "DEPOT"]);

        Assert.Equal(["FARM", "DEPOT"], await WarehouseClaimsAsync(user));
    }

    [Fact]
    public async Task An_account_with_neither_names_no_warehouse()
    {
        var user = AddUser(ApplicationRoles.SalesRep);

        Assert.Empty(await WarehouseClaimsAsync(user));
    }

    private User AddUser(string role, int? shopId = null, List<string>? warehouses = null)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"{role.ToLowerInvariant()}.{Guid.NewGuid():N}",
            PasswordHash = "not-a-real-hash",
            Role = role,
            IsActive = true,
            ShopId = shopId
        };
        user.SetWarehouseCodes(warehouses);

        _context.Users.Add(user);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        return user;
    }

    /// <summary>Issues a token through a refresh, the path a till takes all day, and reads it back.</summary>
    private async Task<List<string>> WarehouseClaimsAsync(User user)
    {
        var refreshValue = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        _context.RefreshTokens.Add(new ShopInventory.Models.RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(refreshValue))),
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedByIp = Ip
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var response = await CreateService().RefreshTokenAsync(refreshValue, Ip);
        Assert.NotNull(response);

        return new JwtSecurityTokenHandler()
            .ReadJwtToken(response!.AccessToken)
            .Claims
            .Where(claim => claim.Type == "warehouse")
            .Select(claim => claim.Value)
            .ToList();
    }

    private AuthService CreateService() =>
        new(_context,
            Options.Create(new JwtSettings
            {
                SecretKey = new string('k', 64),
                Issuer = "ShopInventoryAPI",
                Audience = "ShopInventoryClients",
                AccessTokenExpirationMinutes = 60,
                RefreshTokenExpirationDays = 7
            }),
            Options.Create(new SecuritySettings()),
            new CapturingLogger<AuthService>(),
            StubProxy.Unused<ITwoFactorPendingStore>(),
            StubProxy.Unused<ITwoFactorService>());
}
