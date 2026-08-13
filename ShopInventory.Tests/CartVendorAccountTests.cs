using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins what a cart vendor account is allowed to sell, and to whom.
///
/// A vending operator invoices vendors from a list kept for its business partner, not whoever walks
/// in. The list is the control: an administrator adds a vendor code, and deactivating one has to stop
/// it accepting transactions — which is only true if the deactivation is enforced where the sale
/// resolves the vendor, not merely hidden in a UI.
/// </summary>
public sealed class CartVendorAccountTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public CartVendorAccountTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static User VendingAccount(string businessPartner = "VEND-BP") => new()
    {
        Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Username = "cart01",
        PasswordHash = "not-used-here",
        IsActive = true,
        Role = ApplicationRoles.CartVendor,
        AssignedBusinessPartnerCode = businessPartner,
        AssignedCostCentreCode = "CC-VEND",
        AssignedWarehouseCodes = JsonSerializer.Serialize(new[] { "VAN008" }),
    };

    private void SeedVendor(string code, string businessPartner = "VEND-BP", bool isActive = true)
        => _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = businessPartner,
            Code = code,
            Name = $"Vendor {code}",
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
        });

    // ---- The account ------------------------------------------------------------------------------

    [Fact]
    public void A_cart_vendor_sells_from_its_own_vendor_list()
    {
        Assert.True(ApplicationRoles.UsesRouteCustomerScope(ApplicationRoles.CartVendor));
    }

    [Fact]
    public void A_cart_vendor_account_needs_a_business_partner_a_cost_centre_and_a_warehouse()
    {
        // All three are read on every sale: the business partner is who the invoice is raised to and
        // what scopes the vendor list, the warehouse is where the stock leaves from, and the cost
        // centre is what it books against.
        Assert.True(ApplicationRoles.RequiresAssignedBusinessPartnerCode(ApplicationRoles.CartVendor));
        Assert.True(ApplicationRoles.RequiresAssignedCostCentreCode(ApplicationRoles.CartVendor));
        Assert.True(ApplicationRoles.RequiresWarehouseAssignments(ApplicationRoles.CartVendor));
    }

    [Fact]
    public void A_cart_vendor_account_does_not_need_a_supplying_depot()
    {
        // The one requirement that does not carry over from the van roles. A van is loaded at a depot
        // before it goes out; a cart vendor sells from its own business partner's warehouse and is
        // never loaded from somewhere else, so demanding one would block account creation on a value
        // nothing reads.
        Assert.False(ApplicationRoles.RequiresSupplyingWarehouseCode(ApplicationRoles.CartVendor));
        Assert.True(ApplicationRoles.RequiresSupplyingWarehouseCode(ApplicationRoles.Adr));
    }

    [Fact]
    public void A_cart_vendor_account_can_be_created_and_can_reach_the_api()
    {
        // Without both of these the role exists but is unusable: an administrator cannot assign it,
        // and an account holding it is refused at every endpoint.
        Assert.Contains(ApplicationRoles.CartVendor, ApplicationRoles.AssignableRoles);
        Assert.Contains(ApplicationRoles.CartVendor, ApplicationRoles.ApiAccessRoles);
    }

    [Fact]
    public void A_cart_vendor_resolves_a_selling_identity()
    {
        var resolved = SellingAccountResolver.Resolve(VendingAccount());

        Assert.False(resolved.IsError);
        Assert.Equal("VEND-BP", resolved.Value.CardCode);
        Assert.Equal("VAN008", resolved.Value.WarehouseCode);
        Assert.Equal("CC-VEND", resolved.Value.CostCentreCode);
    }

    // ---- The vendor list --------------------------------------------------------------------------

    [Fact]
    public async Task An_account_sees_the_vendors_of_its_own_business_partner()
    {
        SeedVendor("SHOP-A");
        SeedVendor("SHOP-B");
        SeedVendor("OTHER-ROUTE", businessPartner: "OTHER-BP");
        await _context.SaveChangesAsync();

        var vendors = await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(
            _context, VendingAccount(), CancellationToken.None);

        Assert.Equal(2, vendors.Count);
        Assert.DoesNotContain(vendors, v => v.Code == "OTHER-ROUTE");
    }

    [Fact]
    public async Task A_deactivated_vendor_stops_accepting_transactions()
    {
        // THE requirement. Deactivation has to bite where a sale resolves the vendor, not only where a
        // list is drawn — otherwise a till holding a stale list, or a caller naming the code directly,
        // keeps invoicing a vendor the business has switched off.
        SeedVendor("SHOP-A");
        SeedVendor("SHOP-GONE", isActive: false);
        await _context.SaveChangesAsync();

        var vendors = await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(
            _context, VendingAccount(), CancellationToken.None);

        Assert.Single(vendors);
        Assert.Equal("SHOP-A", vendors[0].Code);
    }

    [Fact]
    public async Task Removing_a_vendor_is_what_deactivates_it()
    {
        // Delete is soft: the row stays so its trading history stays attached to it, and IsActive is
        // what the sale path reads. So "delete" and "deactivate" are the same act, and a returning
        // vendor is reactivated rather than duplicated.
        SeedVendor("SHOP-A");
        await _context.SaveChangesAsync();

        var vendor = await _context.RouteCustomers.SingleAsync();
        vendor.IsActive = false;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var vendors = await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(
            _context, VendingAccount(), CancellationToken.None);

        Assert.Empty(vendors);
        // The row survives, so the takings booked against it are still reachable.
        Assert.Equal(1, await _context.RouteCustomers.CountAsync());
    }

    // ---- Naming one vendor on a sale ---------------------------------------------------------------

    [Fact]
    public async Task A_sale_can_name_an_active_vendor_of_its_own_business_partner()
    {
        SeedVendor("SHOP-A");
        await _context.SaveChangesAsync();

        var vendor = await VanSalesRouteCustomerScope.FindAssignableAsync(
            _context, "VEND-BP", "SHOP-A", CancellationToken.None);

        Assert.NotNull(vendor);
        Assert.Equal("SHOP-A", vendor.Code);
    }

    [Fact]
    public async Task A_sale_cannot_name_a_deactivated_vendor()
    {
        // The other half of the requirement. Hiding a vendor from the list is not enough on its own:
        // a till holding a list from before the deactivation would still name the code, and the
        // server has to be the one that refuses.
        SeedVendor("SHOP-GONE", isActive: false);
        await _context.SaveChangesAsync();

        var vendor = await VanSalesRouteCustomerScope.FindAssignableAsync(
            _context, "VEND-BP", "SHOP-GONE", CancellationToken.None);

        Assert.Null(vendor);
    }

    [Fact]
    public async Task A_sale_cannot_name_another_business_partners_vendor()
    {
        SeedVendor("OTHER-ROUTE", businessPartner: "OTHER-BP");
        await _context.SaveChangesAsync();

        var vendor = await VanSalesRouteCustomerScope.FindAssignableAsync(
            _context, "VEND-BP", "OTHER-ROUTE", CancellationToken.None);

        Assert.Null(vendor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_vendor_code_resolves_to_nothing(string? code)
    {
        SeedVendor("SHOP-A");
        await _context.SaveChangesAsync();

        Assert.Null(await VanSalesRouteCustomerScope.FindAssignableAsync(
            _context, "VEND-BP", code, CancellationToken.None));
    }

    [Fact]
    public async Task An_account_with_no_business_partner_sees_no_vendors()
    {
        SeedVendor("SHOP-A");
        await _context.SaveChangesAsync();

        var unassigned = VendingAccount();
        unassigned.AssignedBusinessPartnerCode = null;

        var vendors = await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(
            _context, unassigned, CancellationToken.None);

        Assert.Empty(vendors);
    }
}
