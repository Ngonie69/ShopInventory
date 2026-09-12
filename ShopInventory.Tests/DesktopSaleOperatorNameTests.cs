using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that a listed sale says who captured it, by name.
///
/// <c>DesktopSaleEntity.CreatedBy</c> holds <c>account.UserId.ToString()</c> — every writer of the
/// column stores an id — so the console's "Captured by" fact showed operators a bare GUID. The list
/// now resolves it, and these are the four things that resolution has to get right: a person with a
/// name, a person with only a login, an id whose account is gone, and the rows written before
/// accounts were ids at all.
/// </summary>
public sealed class DesktopSaleOperatorNameTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public DesktopSaleOperatorNameTests()
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

    [Fact]
    public async Task A_sale_is_named_for_the_person_who_captured_it()
    {
        var cashier = await AddUser("tmoyo", "Tafadzwa", "Moyo");
        await AddSale("TILL-001", cashier.ToString());

        var sale = Assert.Single((await List()).Sales);

        Assert.Equal("Tafadzwa Moyo", sale.CreatedByName);

        // The id keeps its place beside the name: it is what a support call matches to the till's
        // own logs, and the console shows it when there is nothing better.
        Assert.Equal(cashier.ToString(), sale.CreatedBy);
    }

    [Fact]
    public async Task An_account_with_no_name_on_it_falls_back_to_the_login()
    {
        var cashier = await AddUser("till07", firstName: null, lastName: null);
        await AddSale("TILL-002", cashier.ToString());

        Assert.Equal("till07", Assert.Single((await List()).Sales).CreatedByName);
    }

    [Fact]
    public async Task An_account_that_no_longer_exists_is_left_unnamed()
    {
        // Null rather than the id echoed back as if it were a name. The console says "Account no
        // longer exists" and puts the id underneath, which the id alone could not say.
        var gone = Guid.NewGuid();
        await AddSale("TILL-003", gone.ToString());

        var sale = Assert.Single((await List()).Sales);

        Assert.Null(sale.CreatedByName);
        Assert.Equal(gone.ToString(), sale.CreatedBy);
    }

    [Fact]
    public async Task A_row_written_before_accounts_were_ids_keeps_the_name_it_carries()
    {
        await AddSale("TILL-004", "shopfloor");

        Assert.Equal("shopfloor", Assert.Single((await List()).Sales).CreatedByName);
    }

    [Fact]
    public async Task A_sale_captured_by_nobody_is_named_nobody()
    {
        await AddSale("TILL-005", createdBy: null);

        Assert.Null(Assert.Single((await List()).Sales).CreatedByName);
    }

    [Fact]
    public async Task Every_row_on_the_page_is_named_from_one_lookup()
    {
        // The resolution is per page rather than per row. Two sales by one cashier and a third by
        // another is the shape that would catch a lookup that only ever answered for the first row.
        var moyo = await AddUser("tmoyo", "Tafadzwa", "Moyo");
        var banda = await AddUser("cbanda", "Chipo", "Banda");
        await AddSale("TILL-006", moyo.ToString());
        await AddSale("TILL-007", banda.ToString());
        await AddSale("TILL-008", moyo.ToString());

        var named = (await List()).Sales.ToDictionary(s => s.ExternalReferenceId, s => s.CreatedByName);

        Assert.Equal("Tafadzwa Moyo", named["TILL-006"]);
        Assert.Equal("Chipo Banda", named["TILL-007"]);
        Assert.Equal("Tafadzwa Moyo", named["TILL-008"]);
    }

    // ---- Harness --------------------------------------------------------------------------------

    private async Task<Guid> AddUser(
        string username, string? firstName, string? lastName, string role = ApplicationRoles.TillOperator)
    {
        var id = Guid.NewGuid();
        _context.Users.Add(new User
        {
            Id = id,
            Username = username,
            PasswordHash = "x",
            Role = role,
            FirstName = firstName,
            LastName = lastName,
            IsActive = true,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private async Task AddSale(string reference, string? createdBy)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosShopTill",
            CardCode = "COR007",
            WarehouseCode = "KEFGRS",
            DocDate = new DateTime(2026, 9, 11),
            TotalAmount = 411.48m,
            VatAmount = 47.36m,
            AmountPaid = 412m,
            Currency = "USD",
            CreatedBy = createdBy,
            CreatedAt = new DateTime(2026, 9, 11, 7, 0, 0, DateTimeKind.Utc),
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    /// <summary>Lists as the console does: a Cashier with no shop reads across every shop.</summary>
    private async Task<DesktopSalesListResult> List()
    {
        var console = await AddUser($"console{Guid.NewGuid():N}"[..12], null, null, ApplicationRoles.Cashier);

        var result = await new GetDesktopSalesHandler(_context, new RecordingAuditService())
            .Handle(new GetDesktopSalesQuery(console), CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }
}
