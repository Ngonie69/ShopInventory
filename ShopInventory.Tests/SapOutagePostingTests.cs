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

    private DesktopSalePostingService Service(Func<CreateInvoiceRequest, Task<Invoice>>? createInvoice = null)
    {
        var docNum = 5000;
        createInvoice ??= request =>
        {
            _created.Add(request);
            docNum++;
            return Task.FromResult(new Invoice { DocEntry = docNum, DocNum = docNum });
        };

        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) => (object)Task.FromResult<Invoice?>(null),
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
