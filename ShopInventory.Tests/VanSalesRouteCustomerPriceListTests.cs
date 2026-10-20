using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomers;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What the handset is told to price a web-app customer by.
///
/// The handset files its stored van catalogue under the account and the price list it sends, and its
/// own reads — dashboard, stock screen, a load landing — send the van account alone. Sending the
/// account's list number beside it for every route customer made each sale a different key from
/// those reads for identical prices: the server prices from the account's profile and never reads
/// the number. A rep who opened a sale without signal, or whose read failed, had every price withheld
/// and could not submit. VAN018, Athiens, 2026-10-20: 31 lines, every one priced on list 48.
/// </summary>
public sealed class VanSalesRouteCustomerPriceListTests : IDisposable
{
    private const string VanAccount = "VAN018";
    private static readonly Guid Rep = Guid.Parse("18181818-1818-1818-1818-181818181818");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesRouteCustomerPriceListTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = Rep,
            Username = "van018",
            Email = "van018@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = VanAccount,
            AssignedCostCentreCode = "CC018",
            AssignedBusinessPartnerCode = VanAccount
        });

        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = VanAccount,
            Code = "ATHIENS",
            Name = "Athiens",
            IsActive = true,
            CreatedByUserId = Rep,
            CreatedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc)
        });

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task An_account_with_its_own_price_profile_sends_no_price_list()
    {
        _context.BusinessPartnerPriceProfiles.Add(new BusinessPartnerPriceProfileEntity
        {
            CardCode = VanAccount,
            CardName = "Van Sales Bulawayo - Local",
            PriceListNum = 48,
            IsActive = true,
            SyncedFromSAP = true
        });
        await _context.SaveChangesAsync();

        var shop = Assert.Single(await ListAsync());

        Assert.Equal(VanAccount, shop.BpNumber);
        Assert.Equal(string.Empty, shop.PriceList);
    }

    /// <summary>
    /// With no profile the server prices from the number the handset sends, so it has to go.
    /// </summary>
    [Fact]
    public async Task An_account_with_no_price_profile_still_sends_its_list()
    {
        var shop = Assert.Single(await ListAsync());

        Assert.Equal(VanAccount, shop.BpNumber);
        Assert.Equal("48", shop.PriceList);
    }

    /// <summary>
    /// The pricing lookup ignores a profile that did not come from SAP, so this must too.
    /// </summary>
    [Fact]
    public async Task A_profile_not_synced_from_SAP_does_not_count()
    {
        _context.BusinessPartnerPriceProfiles.Add(new BusinessPartnerPriceProfileEntity
        {
            CardCode = VanAccount,
            PriceListNum = 48,
            IsActive = true,
            SyncedFromSAP = false
        });
        await _context.SaveChangesAsync();

        var shop = Assert.Single(await ListAsync());

        Assert.Equal("48", shop.PriceList);
    }

    private async Task<List<VanSalesShopDto>> ListAsync()
    {
        var handler = new GetVanSalesCustomersHandler(
            _context,
            VanAccountPartner(),
            NullLogger<GetVanSalesCustomersHandler>.Instance);

        var result = await handler.Handle(new GetVanSalesCustomersQuery(Rep), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        return result.Value;
    }

    /// <summary>As production reads it: the partner read selects PriceListNum, never a list name.</summary>
    private static IBusinessPartnerService VanAccountPartner() =>
        StubProxy.For<IBusinessPartnerService>((method, _) => method.Name switch
        {
            nameof(IBusinessPartnerService.GetBusinessPartnerByCodeAsync) => Task.FromResult<BusinessPartnerDto?>(
                new BusinessPartnerDto
                {
                    CardCode = VanAccount,
                    CardName = "Van Sales Bulawayo - Local",
                    Currency = "##",
                    PriceListNum = 48,
                    IsActive = true
                }),
            _ => throw new InvalidOperationException($"IBusinessPartnerService.{method.Name} was not expected.")
        });
}
