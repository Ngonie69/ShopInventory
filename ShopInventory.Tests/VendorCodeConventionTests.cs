using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.RouteCustomers.Commands.CreateRouteCustomer;
using ShopInventory.Features.RouteCustomers.Commands.UpdateRouteCustomer;
using ShopInventory.Features.Vending;
using ShopInventory.Features.Vending.Commands.ImportVendors;
using ShopInventory.Features.Vending.Queries.GetVendingOverview;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A vending vendor's code is its depot's warehouse prefix and three digits: VMB (KEFBYC), VMP (KEFGRC)
/// or VMM (CORMACH), such as VMB001.
/// </summary>
/// <remarks>
/// The rule has to hold on every way in — adding one vendor, editing one, and the bulk upload — and not
/// leak onto the van routes' shops, which share the table and keep their name-derived codes.
/// </remarks>
public sealed class VendorCodeConventionTests : IDisposable
{
    private const string Bulawayo = "COR008";
    private const string Graniteside = "COR006";
    private const string VanRoute = "VAN-BP";
    private static readonly Guid Admin = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly NoOpNotificationService _notifications = new();

    public VendorCodeConventionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = Admin,
            Username = "admin",
            PasswordHash = "x",
            Role = ApplicationRoles.Admin,
            IsActive = true,
        });

        Cashier("byo.cashier", Bulawayo, "KEFBYC");
        Cashier("grc.cashier", Graniteside, "KEFGRC");
        Cashier("van01", VanRoute, "VAN001", role: ApplicationRoles.Sales);

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private void Cashier(string username, string bp, string warehouse, bool isActive = true, string role = ApplicationRoles.CartVendor)
        => _context.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = "x",
            Role = role,
            IsActive = isActive,
            AssignedBusinessPartnerCode = bp,
            AssignedCostCentreCode = "CC",
            AssignedWarehouseCodes = JsonSerializer.Serialize(new[] { warehouse }),
        });

    private async Task SeedVendorAsync(string code, string bp, bool isActive = true)
    {
        _context.RouteCustomers.Add(new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = bp,
            Code = code,
            Name = $"Vendor {code}",
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private async Task<ErrorOr.ErrorOr<RouteCustomerDto>> CreateAsync(string bp, string? code, string name = "Tendai")
    {
        var result = await new CreateRouteCustomerHandler(_context, _notifications, NullLogger<CreateRouteCustomerHandler>.Instance)
            .Handle(new CreateRouteCustomerCommand(
                new CreateRouteCustomerRequest { AssignedBusinessPartnerCode = bp, Code = code, Name = name },
                Admin), CancellationToken.None);
        _context.ChangeTracker.Clear();
        return result;
    }

    private async Task<ImportVendorsResultDto> ImportAsync(bool validateOnly, params ImportVendorRow[] rows)
    {
        var result = await new ImportVendorsHandler(_context, _notifications, NullLogger<ImportVendorsHandler>.Instance)
            .Handle(new ImportVendorsCommand(new ImportVendorsRequest { ValidateOnly = validateOnly, Rows = [.. rows] }, Admin),
                CancellationToken.None);
        _context.ChangeTracker.Clear();
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private static ImportVendorRow Row(int rowNumber, string? code = null, string? depot = null, string? name = "Tendai")
        => new() { RowNumber = rowNumber, Code = code, Depot = depot, Name = name };

    // ── The convention itself ──────────────────────────────────────────────

    [Theory]
    [InlineData("VMB001", true)]
    [InlineData("vmp014", true)]
    [InlineData(" VMM999 ", true)]
    [InlineData("VMB000", false)]
    [InlineData("VMB1", false)]
    [InlineData("VMB0001", false)]
    [InlineData("VMB-01", false)]
    [InlineData("WMM001", false)]
    [InlineData("ABC001", false)]
    [InlineData("", false)]
    public void A_code_is_a_known_prefix_and_three_digits(string code, bool valid)
    {
        Assert.Equal(valid, VendorCodeConvention.TryParse(code, out _, out _));
    }

    [Fact]
    public void Each_warehouse_has_its_own_prefix()
    {
        Assert.Equal("VMB", VendorCodeConvention.PrefixForWarehouse("KEFBYC"));
        Assert.Equal("VMP", VendorCodeConvention.PrefixForWarehouse("KEFGRC"));
        Assert.Equal("VMM", VendorCodeConvention.PrefixForWarehouse("CORMACH"));
        Assert.Null(VendorCodeConvention.PrefixForWarehouse("CORMACH2"));
    }

    // ── Adding one ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_vendor_added_without_a_code_takes_the_depots_next_number()
    {
        Assert.Equal("VMB001", (await CreateAsync(Bulawayo, null)).Value.Code);
        Assert.Equal("VMB002", (await CreateAsync(Bulawayo, null, "Rudo")).Value.Code);
        Assert.Equal("VMP001", (await CreateAsync(Graniteside, null)).Value.Code);
    }

    [Fact]
    public async Task The_next_number_follows_the_highest_issued_not_the_count()
    {
        await SeedVendorAsync("VMB007", Bulawayo, isActive: false);

        Assert.Equal("VMB008", (await CreateAsync(Bulawayo, null)).Value.Code);
    }

    [Fact]
    public async Task A_code_with_another_depots_prefix_is_refused()
    {
        var result = await CreateAsync(Bulawayo, "VMP001");

        Assert.True(result.IsError);
        Assert.Equal("Vending.VendorCodeDoesNotFitDepot", result.FirstError.Code);
        Assert.Empty(_context.RouteCustomers);
    }

    [Fact]
    public async Task A_code_off_the_convention_is_refused()
    {
        var result = await CreateAsync(Bulawayo, "TENDAI");

        Assert.True(result.IsError);
        Assert.Contains("VMB001", result.FirstError.Description);
    }

    [Fact]
    public async Task A_conforming_code_is_kept()
    {
        Assert.Equal("VMB042", (await CreateAsync(Bulawayo, "vmb042")).Value.Code);
    }

    [Fact]
    public async Task A_code_is_unique_across_depots_on_the_same_warehouse()
    {
        Cashier("byo2.cashier", "COR099", "KEFBYC");
        await _context.SaveChangesAsync();
        await SeedVendorAsync("VMB001", Bulawayo);

        var result = await CreateAsync("COR099", "VMB001");

        Assert.True(result.IsError);
        Assert.Equal("Vending.VendorCodeTaken", result.FirstError.Code);
        Assert.Equal("VMB002", (await CreateAsync("COR099", null)).Value.Code);
    }

    [Fact]
    public async Task A_depot_on_a_warehouse_with_no_prefix_cannot_add_vendors()
    {
        Cashier("machipisa.cashier", "COR010", "CORMACH2");
        await _context.SaveChangesAsync();

        var result = await CreateAsync("COR010", null);

        Assert.True(result.IsError);
        Assert.Equal("Vending.DepotCannotNumberVendors", result.FirstError.Code);
    }

    [Fact]
    public async Task A_van_routes_shops_keep_their_name_derived_codes()
    {
        var result = await CreateAsync(VanRoute, null, "Corner Shop");

        Assert.False(result.IsError);
        Assert.Equal("CORNERSHOP", result.Value.Code);
    }

    // ── Editing one ────────────────────────────────────────────────────────

    [Fact]
    public async Task Moving_a_vendor_to_a_depot_on_another_warehouse_is_refused()
    {
        var created = (await CreateAsync(Bulawayo, null)).Value;

        var result = await new UpdateRouteCustomerHandler(_context).Handle(
            new UpdateRouteCustomerCommand(created.Id, new UpdateRouteCustomerRequest
            {
                AssignedBusinessPartnerCode = Graniteside,
                Code = created.Code,
                Name = created.Name,
            }), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Vending.VendorCodeDoesNotFitDepot", result.FirstError.Code);
    }

    [Fact]
    public async Task A_vendor_under_an_older_code_can_still_be_edited_in_place()
    {
        await SeedVendorAsync("LEGACY", Bulawayo);
        var id = _context.RouteCustomers.Single().Id;

        var result = await new UpdateRouteCustomerHandler(_context).Handle(
            new UpdateRouteCustomerCommand(id, new UpdateRouteCustomerRequest
            {
                AssignedBusinessPartnerCode = Bulawayo,
                Code = "LEGACY",
                Name = "Renamed",
                Phone = "0771 000 000",
            }), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
    }

    // ── The overview ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_overview_shows_each_depots_prefix_and_next_code()
    {
        await SeedVendorAsync("VMB003", Bulawayo);

        var overview = (await new GetVendingOverviewHandler(_context)
            .Handle(new GetVendingOverviewQuery(), CancellationToken.None)).Value;

        var byo = overview.Depots.Single(depot => depot.BusinessPartnerCode == Bulawayo);
        Assert.Equal("VMB", byo.VendorCodePrefix);
        Assert.Equal("VMB004", byo.NextVendorCode);

        var grc = overview.Depots.Single(depot => depot.BusinessPartnerCode == Graniteside);
        Assert.Equal("VMP", grc.VendorCodePrefix);
        Assert.Equal("VMP001", grc.NextVendorCode);
    }

    // ── The upload ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Checking_a_file_saves_nothing()
    {
        var result = await ImportAsync(validateOnly: true, Row(2, "VMB001"), Row(3, depot: Graniteside));

        Assert.False(result.Imported);
        Assert.Equal(2, result.CreateCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.Empty(_context.RouteCustomers);
    }

    [Fact]
    public async Task The_code_prefix_places_a_row_at_its_depot()
    {
        var result = await ImportAsync(validateOnly: false, Row(2, "VMP005"), Row(3, "VMB001"));

        Assert.True(result.Imported);
        Assert.Equal([Graniteside, Bulawayo], result.Rows.Select(row => row.Depot));
        Assert.Equal(2, _context.RouteCustomers.Count());
        Assert.All(_context.RouteCustomers, vendor => Assert.Equal(Admin, vendor.CreatedByUserId));
    }

    [Fact]
    public async Task A_depot_can_be_named_by_its_warehouse()
    {
        var result = await ImportAsync(validateOnly: true, Row(2, depot: "kefbyc"));

        Assert.Equal(Bulawayo, result.Rows[0].Depot);
        Assert.Equal("VMB001", result.Rows[0].Code);
    }

    [Fact]
    public async Task Blank_codes_are_numbered_after_every_code_the_file_names()
    {
        await SeedVendorAsync("VMB002", Bulawayo);

        var result = await ImportAsync(validateOnly: false,
            Row(2, depot: Bulawayo),
            Row(3, "VMB010"),
            Row(4, depot: Bulawayo));

        Assert.Equal(["VMB011", "VMB010", "VMB012"], result.Rows.Select(row => row.Code));
        Assert.Equal([true, false, true], result.Rows.Select(row => row.CodeIssued));
    }

    [Fact]
    public async Task One_bad_row_imports_nothing_and_every_problem_is_reported()
    {
        await SeedVendorAsync("VMB001", Bulawayo);

        var result = await ImportAsync(validateOnly: false,
            Row(2, "VMB002"),
            Row(3, "VMB001"),                       // already a live vendor
            Row(4, "VMP003", depot: Bulawayo),      // wrong prefix for the named depot
            Row(5, "VMB9"),                         // not a code
            Row(6, depot: Graniteside, name: null), // no name
            Row(7, "VMB020"),
            Row(8, "vmb020"));                      // duplicate in the file

        Assert.False(result.Imported);
        Assert.Equal(6, result.ErrorCount);
        Assert.Equal(ImportVendorActions.Create, result.Rows[0].Action);
        Assert.Contains("already a vendor", result.Rows[1].Errors.Single());
        Assert.Contains("VMB and three digits", result.Rows[2].Errors.Single());
        Assert.Contains("is not a vendor code", result.Rows[3].Errors.Single());
        Assert.Contains("first name", result.Rows[4].Errors.Single());
        Assert.Contains("also on row 8", result.Rows[5].Errors.Single());
        Assert.Contains("also on row 7", result.Rows[6].Errors.Single());
        Assert.Single(_context.RouteCustomers);
    }

    [Fact]
    public async Task A_removed_vendors_code_brings_them_back()
    {
        await SeedVendorAsync("VMB004", Bulawayo, isActive: false);

        var result = await ImportAsync(validateOnly: false, Row(2, "VMB004", name: "Returned"));

        Assert.Equal(1, result.RestoreCount);
        var vendor = _context.RouteCustomers.Single();
        Assert.True(vendor.IsActive);
        Assert.Equal("Returned", vendor.Name);
    }

    [Fact]
    public async Task A_van_route_is_not_a_depot_to_upload_to()
    {
        var result = await ImportAsync(validateOnly: true, Row(2, depot: VanRoute));

        Assert.Contains("is not a vending depot", result.Rows[0].Errors.Single());
    }

    [Fact]
    public async Task An_import_sends_one_notification_per_cashier_not_per_vendor()
    {
        await ImportAsync(validateOnly: false, Row(2, depot: Bulawayo), Row(3, depot: Bulawayo), Row(4, depot: Bulawayo));

        var sent = Assert.Single(_notifications.Sent);
        Assert.Equal("byo.cashier", sent.TargetUsername);
        Assert.Contains("3 vendors", sent.Message);
    }
}
