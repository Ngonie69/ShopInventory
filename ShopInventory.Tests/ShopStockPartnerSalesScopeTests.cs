using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins what a ShopStock account reads at /shop-sales: the sales of the one business partner set on its
/// account as <see cref="User.SalesBusinessPartnerCode"/>, wherever they were rung up, and nothing else.
///
/// The partner scope carries no warehouse, so to a reader that does not apply it, it would look like every
/// shop's takings. The assertions that matter are therefore the ones about what stays refused: another
/// partner, an account with no partner, and every read that does not go through
/// <see cref="DesktopSalesReadScopeResolver.ResolveForPartnerReads"/>.
/// </summary>
public sealed class ShopStockPartnerSalesScopeTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 9, 1);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ShopStockPartnerSalesScopeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        // Two of FARM-BP's sales at two different warehouses, so a scope that confined by warehouse
        // instead of by partner would lose one; and one of another partner's, so a leak shows as a row.
        _context.DesktopSales.AddRange(
            Sale("FARM-001", "KEFSHOP", "FARM-BP", 100m),
            Sale("FARM-002", "CORMACH2", "FARM-BP", 40m),
            Sale("MACH-001", "CORMACH2", "MACH-BP", 900m));
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---- The list ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_stock_user_sees_every_sale_of_its_partner_and_no_other()
    {
        var stockUser = await AddStockUser(salesPartner: "FARM-BP");

        var result = await List(stockUser, cardCode: null);

        Assert.False(result.IsError);
        Assert.Equal(
            ["FARM-001", "FARM-002"],
            result.Value.Sales.Select(s => s.ExternalReferenceId).OrderBy(r => r).ToArray());
        Assert.Equal(2, result.Value.TotalCount);
    }

    [Fact]
    public async Task A_stock_user_naming_its_own_partner_is_allowed()
    {
        var stockUser = await AddStockUser(salesPartner: "FARM-BP");

        var result = await List(stockUser, cardCode: "FARM-BP");

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.TotalCount);
    }

    [Fact]
    public async Task A_stock_user_naming_another_partner_is_refused()
    {
        var stockUser = await AddStockUser(salesPartner: "FARM-BP");

        var result = await List(stockUser, cardCode: "MACH-BP");

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SalesPartnerOutsideScope", result.FirstError.Code);
    }

    [Fact]
    public async Task A_stock_user_filtering_by_warehouse_stays_inside_its_partner()
    {
        // CORMACH2 holds a sale of each partner; the warehouse is a filter, the partner the scope.
        var stockUser = await AddStockUser(salesPartner: "FARM-BP");

        var result = await List(stockUser, cardCode: null, warehouseCode: "CORMACH2");

        Assert.False(result.IsError);
        Assert.Equal("FARM-002", Assert.Single(result.Value.Sales).ExternalReferenceId);
    }

    [Fact]
    public async Task A_stock_user_with_no_partner_is_told_so()
    {
        var stockUser = await AddStockUser(salesPartner: null);

        var result = await List(stockUser, cardCode: null);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SalesPartnerNotAssigned", result.FirstError.Code);
    }

    [Fact]
    public async Task The_selling_partner_column_does_not_grant_the_read()
    {
        // AssignedBusinessPartnerCode is what an account sells under. A stock user holding it (a leftover
        // from another role) must not read that partner's sales by it.
        var stockUser = await AddStockUser(salesPartner: null, sellingPartner: "FARM-BP");

        var result = await List(stockUser, cardCode: null);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SalesPartnerNotAssigned", result.FirstError.Code);
    }

    [Fact]
    public async Task An_inactive_stock_user_reads_nothing()
    {
        var stockUser = await AddStockUser(salesPartner: "FARM-BP", isActive: false);

        var result = await List(stockUser, cardCode: null);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.Unauthenticated", result.FirstError.Code);
    }

    // ---- The analysis -----------------------------------------------------------------------------

    [Fact]
    public async Task The_analysis_is_confined_to_the_partner_and_says_which()
    {
        var stockUser = await AddStockUser(salesPartner: "FARM-BP");

        var result = await Analyse(stockUser, cardCode: null);

        Assert.False(result.IsError);
        Assert.Equal("FARM-BP", result.Value.CardCode);
        var usd = Assert.Single(result.Value.Currencies);
        Assert.Equal(2, usd.SalesCount);
        Assert.Equal(140m, usd.TotalAmount);
    }

    [Fact]
    public async Task The_analysis_refuses_another_partner()
    {
        var stockUser = await AddStockUser(salesPartner: "FARM-BP");

        var result = await Analyse(stockUser, cardCode: "MACH-BP");

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SalesPartnerOutsideScope", result.FirstError.Code);
    }

    [Fact]
    public async Task A_manager_still_reads_every_partner_with_no_card_code()
    {
        var managerId = await AddUser(ApplicationRoles.Manager, salesPartner: null);

        var result = await Analyse(managerId, cardCode: null);

        Assert.False(result.IsError);
        Assert.Null(result.Value.CardCode);
        Assert.Equal(3, Assert.Single(result.Value.Currencies).SalesCount);
    }

    // ---- Everywhere else a stock user is still refused --------------------------------------------

    [Fact]
    public void The_ordinary_resolver_still_refuses_a_stock_user_with_a_partner()
    {
        // Posting, retrying, credit notes and the month-on-month report call Resolve, which knows nothing
        // of partners. A stock user reaching them must be refused, not read as unrestricted.
        var user = StockUser("FARM-BP");

        var scope = DesktopSalesReadScopeResolver.Resolve(user);

        Assert.True(scope.IsError);
        Assert.Equal("DesktopSales.SalesReadNotPermitted", scope.FirstError.Code);
    }

    [Fact]
    public void A_partner_scope_is_not_unrestricted()
    {
        var scope = DesktopSalesReadScopeResolver.ResolveForPartnerReads(StockUser("FARM-BP"));

        Assert.False(scope.IsError);
        Assert.False(scope.Value.IsUnrestricted);
        Assert.Null(scope.Value.WarehouseCode);
        Assert.Equal("FARM-BP", scope.Value.PartnerCode);
    }

    [Fact]
    public void Narrowing_a_partner_scope_by_warehouse_keeps_the_partner()
    {
        var scope = new DesktopSalesReadScope(null, "FARM-BP");

        var narrowed = scope.Narrow("KEFSHOP");
        var many = scope.NarrowMany(["KEFSHOP", "CORMACH2"]);
        var none = scope.NarrowMany([]);

        Assert.Equal(new DesktopSalesReadScope("KEFSHOP", "FARM-BP"), narrowed.Value);
        Assert.Equal(["KEFSHOP", "CORMACH2"], many.Value);
        Assert.Empty(none.Value);
    }

    [Fact]
    public void A_stock_user_with_a_sales_partner_still_cannot_sell()
    {
        // The reason the partner has its own column: the selling resolver reads AssignedBusinessPartnerCode
        // for any role, and a stock user has a warehouse. Given only the read grant, it still cannot sell.
        var user = StockUser("FARM-BP");
        user.SetWarehouseCodes(["KEFSHOP"]);

        var selling = SellingAccountResolver.Resolve(user);

        Assert.True(selling.IsError);
        Assert.Equal("DesktopSales.MissingCustomerAssignment", selling.FirstError.Code);
    }

    // ---- Harness ----------------------------------------------------------------------------------

    private static User StockUser(string? salesPartner) => new()
    {
        Id = Guid.NewGuid(),
        Username = "stock",
        PasswordHash = "x",
        Role = ApplicationRoles.ShopStock,
        IsActive = true,
        SalesBusinessPartnerCode = salesPartner,
    };

    private static DesktopSaleEntity Sale(string reference, string warehouseCode, string cardCode, decimal total) => new()
    {
        ExternalReferenceId = reference,
        SourceSystem = "KefShop",
        CardCode = cardCode,
        WarehouseCode = warehouseCode,
        DocDate = Day,
        TotalAmount = total,
        VatAmount = Math.Round(total * 0.13m, 2),
        AmountPaid = total,
        Currency = "USD",
        CreatedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
    };

    private Task<Guid> AddStockUser(string? salesPartner, string? sellingPartner = null, bool isActive = true) =>
        AddUser(ApplicationRoles.ShopStock, salesPartner, sellingPartner, isActive);

    private async Task<Guid> AddUser(
        string role, string? salesPartner, string? sellingPartner = null, bool isActive = true)
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = $"u{id:N}"[..12],
            PasswordHash = "x",
            Role = role,
            IsActive = isActive,
            SalesBusinessPartnerCode = salesPartner,
            AssignedBusinessPartnerCode = sellingPartner,
        };
        user.SetWarehouseCodes(["KEFSHOP"]);
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private Task<ErrorOr.ErrorOr<DesktopSalesListResult>> List(
        Guid callerId, string? cardCode, string? warehouseCode = null) =>
        new GetDesktopSalesHandler(
                _context,
                new RecordingAuditService(),
                Microsoft.Extensions.Options.Options.Create(new ShopInventory.Configuration.FiscalisationSettings()),
                Microsoft.Extensions.Options.Options.Create(new ShopInventory.Configuration.DesktopSalePostingSettings()),
                Microsoft.Extensions.Options.Options.Create(new ShopInventory.Configuration.VanSalesPostingSettings()))
            .Handle(new GetDesktopSalesQuery(callerId, warehouseCode, cardCode), CancellationToken.None);

    private Task<ErrorOr.ErrorOr<DesktopSalesAnalysisResult>> Analyse(Guid callerId, string? cardCode) =>
        new GetDesktopSalesAnalysisHandler(_context, new RecordingAuditService())
            .Handle(new GetDesktopSalesAnalysisQuery(callerId, Day, Day, CardCode: cardCode), CancellationToken.None);
}
