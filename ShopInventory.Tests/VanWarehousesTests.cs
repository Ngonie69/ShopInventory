using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Covers <see cref="VanWarehouses"/>, the one rule the Van Stock, Van Replenishment and Count
/// Variance reports all use to decide which warehouses are vans.
/// </summary>
public sealed class VanWarehousesTests : IDisposable
{
    private const string Depot = "KEFGRC";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanWarehousesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// A van needs both halves: a rep assigned to it and a depot supplying that rep. The codes here
    /// are deliberately not <c>VAN…</c>, and the store deliberately is, so a prefix rule fails both.
    /// </summary>
    [Fact]
    public async Task A_van_is_an_assigned_warehouse_whose_rep_a_depot_supplies()
    {
        AddUser(1, "rep1", ["KEFVAN10"], supplying: Depot);
        AddUser(2, "cashier", ["VANSTORE"], supplying: null);
        AddUser(3, "unassigned", [], supplying: Depot);
        await _context.SaveChangesAsync();

        var vans = await VanWarehouses.LoadAsync(_context, CancellationToken.None);

        Assert.Equal(["KEFVAN10"], vans.Keys);
    }

    [Fact]
    public async Task Codes_are_trimmed_matched_without_case_and_blanks_are_skipped()
    {
        AddUser(1, "rep1", [" KEFVAN10 ", "", "  ", "KEFVAN11"], supplying: Depot);
        AddUser(2, "rep2", ["kefvan10"], supplying: Depot);
        await _context.SaveChangesAsync();

        var vans = await VanWarehouses.LoadAsync(_context, CancellationToken.None);

        Assert.Equal(["KEFVAN10", "KEFVAN11"], vans.Keys.Order());
        Assert.True(vans.ContainsKey("kefvan11"));
        Assert.Equal("KEFVAN10", vans["kefvan10"].Code);
    }

    [Fact]
    public async Task The_rep_is_named_falling_back_to_the_username_and_two_reps_are_both_named()
    {
        AddUser(1, "tmoyo", ["KEFVAN10"], supplying: Depot, firstName: "Tendai", lastName: "Moyo");
        AddUser(2, "rep2", ["KEFVAN10"], supplying: Depot);
        AddUser(3, "rep3", ["KEFVAN11"], supplying: Depot, firstName: "  ", lastName: null);
        await _context.SaveChangesAsync();

        var vans = await VanWarehouses.LoadAsync(_context, CancellationToken.None);

        Assert.Equal("Tendai Moyo, rep2", vans["KEFVAN10"].Rep);
        Assert.Equal("rep3", vans["KEFVAN11"].Rep);
    }

    /// <summary>
    /// A van's sales invoice to its rep's business partner, not to the warehouse: VAN001 invoices to
    /// VAN010. Two reps on one van can carry two accounts, and a rep with none adds nothing.
    /// </summary>
    [Fact]
    public async Task Each_van_carries_its_reps_business_partner_codes()
    {
        AddUser(1, "rep1", ["VAN001"], supplying: Depot, businessPartner: " VAN010 ");
        AddUser(2, "rep2", ["VAN001"], supplying: Depot, businessPartner: "van010");
        AddUser(3, "rep3", ["VAN001"], supplying: Depot, businessPartner: "VAN019");
        AddUser(4, "rep4", ["VAN004"], supplying: Depot, businessPartner: null);
        await _context.SaveChangesAsync();

        var vans = await VanWarehouses.LoadAsync(_context, CancellationToken.None);

        Assert.Equal(["VAN010", "VAN019"], vans["VAN001"].BusinessPartnerCodes.Order());
        Assert.Empty(vans["VAN004"].BusinessPartnerCodes);
    }

    /// <summary>
    /// Pins today's behaviour rather than endorsing it: <c>IsActive</c> is not read, so a deactivated
    /// rep's van stays a van until the assignment is cleared. Whether it should is an open question;
    /// change this test only alongside a deliberate decision.
    /// </summary>
    [Fact]
    public async Task A_deactivated_reps_van_still_counts()
    {
        AddUser(1, "leaver", ["KEFVAN12"], supplying: Depot, isActive: false);
        await _context.SaveChangesAsync();

        var vans = await VanWarehouses.LoadAsync(_context, CancellationToken.None);

        Assert.Equal("leaver", Assert.Single(vans).Value.Rep);
    }

    [Fact]
    public async Task No_assigned_reps_means_no_vans()
    {
        var vans = await VanWarehouses.LoadAsync(_context, CancellationToken.None);

        Assert.Empty(vans);
    }

    /// <summary>
    /// Ids are fixed so their order is the same in SQLite's text ordering and the databases' native
    /// one — two reps on one van are named in id order.
    /// </summary>
    private void AddUser(
        int id,
        string username,
        List<string> warehouses,
        string? supplying,
        string? firstName = null,
        string? lastName = null,
        string? businessPartner = null,
        bool isActive = true)
    {
        var user = new User
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{id:D12}"),
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "x",
            Role = "SalesRep",
            IsActive = isActive,
            FirstName = firstName,
            LastName = lastName,
            SupplyingWarehouseCode = supplying,
            AssignedBusinessPartnerCode = businessPartner
        };

        user.SetWarehouseCodes(warehouses);
        _context.Users.Add(user);
    }
}
