using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What the till posting pass does during and after a declared SAP outage.
/// </summary>
public sealed class SapOutagePostingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly ApplicationDbContext _context;
    private readonly SapAvailability _availability;
    private readonly List<CreateInvoiceRequest> _created = [];

    public SapOutagePostingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _services = new ServiceCollection()
            .AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection))
            .BuildServiceProvider();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _availability = new SapAvailability(
            _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SapAvailability>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _services.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Nothing_posts_while_an_outage_is_declared_though_this_nodes_circuit_is_closed()
    {
        await GivenSaleAsync(daysAgo: 0);
        _availability.Apply(new SapAvailabilityState(1, DateTime.UtcNow.AddMinutes(-5), SapOutageCauses.Unreachable));

        var result = await Service().PostPendingSalesAsync();

        Assert.Equal(0, result.Total);
        Assert.Empty(_created);
    }

    [Fact]
    public async Task A_sale_older_than_the_window_is_left_alone_when_no_outage_explains_it()
    {
        await GivenSaleAsync(daysAgo: 5);

        await Service().PostPendingSalesAsync();

        Assert.Empty(_created);
    }

    [Fact]
    public async Task A_sale_held_up_by_a_long_outage_posts_once_sap_is_back()
    {
        // Five days old: outside the three-day window, made on the first day of an outage that ended an
        // hour ago. Before, it stayed fiscalised and unposted for good.
        var sale = await GivenSaleAsync(daysAgo: 5);
        _context.SapOutages.Add(new SapOutageEntity
        {
            StartedAtUtc = DateTime.UtcNow.Date.AddDays(-5).AddHours(6),
            DeclaredAtUtc = DateTime.UtcNow.Date.AddDays(-5).AddHours(6),
            EndedAtUtc = DateTime.UtcNow.AddHours(-1),
            Cause = SapOutageCauses.Unreachable,
            LastProbeAtUtc = DateTime.UtcNow.AddHours(-1)
        });
        await _context.SaveChangesAsync();

        var result = await Service().PostPendingSalesAsync();

        Assert.Equal(1, result.Posted);
        Assert.Single(_created);
        var posted = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(DesktopSaleConsolidationStatus.Consolidated, posted.ConsolidationStatus);
    }

    [Fact]
    public async Task A_gateway_failure_on_the_post_spends_no_attempt_and_holds_the_sale_for_a_lookup()
    {
        var sale = await GivenSaleAsync(daysAgo: 0);

        await Service(createInvoice: _ => Task.FromException<Invoice>(new HttpRequestException(
            "SAP did not answer the invoice post: 502 from the gateway.", null, System.Net.HttpStatusCode.BadGateway)))
            .PostPendingSalesAsync();

        var saved = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(0, saved.PostingAttempts);
        Assert.Equal(DesktopSaleConsolidationStatus.Pending, saved.ConsolidationStatus);

        // SAP may hold it: the next pass must ask before it sends again.
        Assert.NotNull(saved.PostIssuedAtUtc);
    }

    [Fact]
    public async Task A_duplicate_check_that_meets_sap_s_database_down_spends_no_attempt()
    {
        // KEF-FAC-20260914-8DFB97306920: SAP's database was down behind a Service Layer that still
        // answered, so the check whether SAP already held the invoice came back 500. Every pass spent
        // an attempt on it — six in six minutes — and the sale stayed unposted for a month.
        var sale = await GivenSaleAsync(daysAgo: 0);
        var databaseDown = new SapReadFailedException(
            System.Net.HttpStatusCode.InternalServerError,
            "Failed to check invoice by U_Van_saleorder: InternalServerError - {\"error\":{\"code\":7,\"message\":"
            + "{\"lang\":\"en-us\",\"value\":\"Failed to connect to kfldb port 40020 after 0 ms: Couldn't connect to server\"}}}");

        for (var pass = 0; pass < 7; pass++)
        {
            await Service(lookup: _ => Task.FromException<Invoice?>(databaseDown)).PostPendingSalesAsync();
            _context.ChangeTracker.Clear();
        }

        var saved = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(0, saved.PostingAttempts);
        Assert.Equal(DesktopSaleConsolidationStatus.Pending, saved.ConsolidationStatus);
        Assert.Contains("could not be asked", saved.LastPostingError);
        Assert.Empty(_created);

        // And once SAP is back, the eighth pass posts it.
        var result = await Service().PostPendingSalesAsync();
        Assert.Equal(1, result.Posted);
    }

    [Fact]
    public async Task A_post_that_meets_sap_s_database_down_spends_no_attempt_and_is_looked_up_before_it_goes_again()
    {
        // The duplicate check above passes, and the outage meets the invoice post itself. SAP's
        // envelope used to make that a refusal: an attempt spent, and the post marker taken off, so
        // the next pass would have sent the sale again without asking whether SAP had kept it.
        var sale = await GivenSaleAsync(daysAgo: 0);
        var posts = 0;

        await Service(createInvoice: _ =>
            {
                posts++;
                return Task.FromException<Invoice>(new SapBackendUnreachableException(
                    "create the invoice",
                    System.Net.HttpStatusCode.InternalServerError,
                    "Failed to connect to kfldb port 40020 after 0 ms: Couldn't connect to server"));
            })
            .PostPendingSalesAsync();
        _context.ChangeTracker.Clear();

        var saved = await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(0, saved.PostingAttempts);
        Assert.Equal(DesktopSaleConsolidationStatus.Pending, saved.ConsolidationStatus);
        Assert.NotNull(saved.PostIssuedAtUtc);
        Assert.Contains("could not reach its database", saved.LastPostingError);

        // Inside the hold, the next pass leaves it alone rather than sending it again.
        await Service().PostPendingSalesAsync();
        _context.ChangeTracker.Clear();
        Assert.Empty(_created);

        // Past the hold, SAP turns out to have kept it after all: adopted, not posted a second time.
        await _context.DesktopSales
            .Where(s => s.Id == sale.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.PostIssuedAtUtc, DateTime.UtcNow.AddHours(-1)));
        var result = await Service(lookup: _ => Task.FromResult<Invoice?>(new Invoice { DocEntry = 7001, DocNum = 8001 }))
            .PostPendingSalesAsync();

        Assert.Equal(1, result.Adopted);
        Assert.Equal(1, posts);
        Assert.Empty(_created);
        Assert.Equal(7001, (await _context.DesktopSales.AsNoTracking().FirstAsync(s => s.Id == sale.Id)).SapDocEntry);
    }

    private async Task<DesktopSaleEntity> GivenSaleAsync(int daysAgo)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = $"KEFSHOP-01-{daysAgo}-000123",
            SourceSystem = SaleSourceSystems.ShopTill,
            CardCode = "KEFSHOP-BP",
            DocDate = DateTime.UtcNow.Date.AddDays(-daysAgo),
            TotalAmount = 25m,
            VatAmount = 3.26m,
            Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            WarehouseCode = "KEFSHOP",
            PaymentMethod = TenderTypes.Cash,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 1,
                    ItemCode = "CHE011",
                    Quantity = 1m,
                    UnitPrice = 25m,
                    LineTotal = 25m,
                    WarehouseCode = "KEFSHOP"
                }
            ]
        };

        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();
        return sale;
    }

    private DesktopSalePostingService Service(
        Func<CreateInvoiceRequest, Task<Invoice>>? createInvoice = null,
        Func<string, Task<Invoice?>>? lookup = null)
    {
        lookup ??= _ => Task.FromResult<Invoice?>(null);
        var docNum = 5000;
        createInvoice ??= request =>
        {
            _created.Add(request);
            docNum++;
            return Task.FromResult(new Invoice { DocEntry = docNum, DocNum = docNum });
        };

        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) => (object)lookup((string)args![0]!),
            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => createInvoice((CreateInvoiceRequest)args![0]!),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        return new DesktopSalePostingService(
            _context,
            sap,
            new SapCircuitBreakerState(Options.Create(new SAPSettings()), null, _availability),
            SaleBatchAllocators.Holding(),
            SalePostGuards.Backed(_connection),
            DesktopCreditPosters.Idle(_context),
            Options.Create(new DesktopSalePostingSettings()),
            NullLogger<DesktopSalePostingService>.Instance,
            Options.Create(new SapAvailabilitySettings()));
    }
}
