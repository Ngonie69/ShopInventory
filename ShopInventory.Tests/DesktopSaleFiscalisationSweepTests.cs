using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.Notifications;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the sweep that signs the sales stored unsigned.
///
/// Vending raises an invoice against a cart vendor, prints nothing, and has nobody waiting at a
/// counter, so its sale is stored Pending and fiscalised moments later. That makes this sweep
/// load-bearing rather than an optimisation: nothing else moves a sale out of Pending, and the SAP
/// posting service only takes sales that have fiscalised — so a vending sale this misses is signed by
/// nobody and invoiced by nobody, indefinitely and with nothing logged.
///
/// The retry rules are the delicate part. Every attempt here is a submission to ZIMRA, and a duplicate
/// fiscal receipt cannot be withdrawn.
/// </summary>
public sealed class DesktopSaleFiscalisationSweepTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public DesktopSaleFiscalisationSweepTests()
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

    private DesktopSaleEntity Seed(
        string externalReference,
        string sourceSystem = SaleSourceSystems.Vending,
        DesktopSaleFiscalizationStatus status = DesktopSaleFiscalizationStatus.Pending,
        int attempts = 0,
        bool requiresReconciliation = false)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = externalReference,
            SourceSystem = sourceSystem,
            CardCode = "VEND-BP",
            DocDate = DateTime.UtcNow.Date,
            WarehouseCode = "VAN008",
            Currency = "USD",
            TotalAmount = 25m,
            AmountPaid = 25m,
            PaymentMethod = TenderTypes.Cash,
            FiscalizationStatus = status,
            FiscalizationAttempts = attempts,
            FiscalizationRequiresReconciliation = requiresReconciliation,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };

        _context.DesktopSales.Add(sale);
        return sale;
    }

    private DesktopSaleFiscalisationSweep BuildSweep(Func<FiscalizationResult> respond, int callBudget = 50)
    {
        var calls = 0;

        var fiscalisation = StubProxy.For<IFiscalizationService>((method, _) => method.Name switch
        {
            nameof(IFiscalizationService.FiscalizePreSapInvoiceAsync) =>
                ++calls <= callBudget
                    ? Task.FromResult(respond())
                    : throw new InvalidOperationException($"The platform was called more than {callBudget} time(s)."),
            _ => throw new InvalidOperationException($"IFiscalizationService.{method.Name} was not expected.")
        });

        var notifications = StubProxy.For<INotificationService>((method, _) => method.Name switch
        {
            nameof(INotificationService.CreateNotificationAsync) => Task.FromResult(0),
            _ => throw new InvalidOperationException($"INotificationService.{method.Name} was not expected.")
        });

        var fiscaliser = new DesktopSaleFiscaliser(
            fiscalisation, notifications, NullLogger<DesktopSaleFiscaliser>.Instance);

        return new DesktopSaleFiscalisationSweep(
            _context,
            fiscaliser,
            Options.Create(new DesktopSalePostingSettings()),
            NullLogger<DesktopSaleFiscalisationSweep>.Instance);
    }

    private static FiscalizationResult Signed() => new()
    {
        Success = true,
        ReceiptGlobalNo = "771",
        DeviceSerial = "DEV-1",
        QRCode = "qr",
        VerificationCode = "vc",
        FiscalDayNo = "12"
    };

    [Fact]
    public async Task A_pending_vending_sale_is_signed_and_becomes_postable()
    {
        Seed("VEND-20260813-0001");
        await _context.SaveChangesAsync();

        var result = await BuildSweep(Signed).FiscalisePendingSalesAsync(CancellationToken.None);

        Assert.Equal(1, result.Fiscalised);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleFiscalizationStatus.Success, sale.FiscalizationStatus);
        Assert.Equal("771", sale.FiscalReceiptNumber);
        // The handoff: the posting service only takes Success or Skipped, so this is what makes a
        // vending sale reach SAP at all.
        Assert.Equal(1, sale.FiscalizationAttempts);
    }

    [Fact]
    public async Task A_sale_that_needs_reconciliation_is_never_offered_again()
    {
        // The one failure worse to retry than to leave alone: the platform could not say whether the
        // receipt was signed, and a second submission cannot be withdrawn.
        Seed("VEND-20260813-0002", requiresReconciliation: true);
        await _context.SaveChangesAsync();

        // callBudget 0 — any call to the platform fails the test.
        var result = await BuildSweep(Signed, callBudget: 0).FiscalisePendingSalesAsync(CancellationToken.None);

        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task A_reconciliation_verdict_stops_the_retries_from_then_on()
    {
        Seed("VEND-20260813-0003");
        await _context.SaveChangesAsync();

        var sweep = BuildSweep(() => new FiscalizationResult
        {
            Success = false,
            Message = "The platform did not answer.",
            RequiresReconciliation = true
        });

        await sweep.FiscalisePendingSalesAsync(CancellationToken.None);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleFiscalizationStatus.Failed, sale.FiscalizationStatus);
        Assert.True(sale.FiscalizationRequiresReconciliation);

        // And the next pass leaves it alone: the budget of 0 means any further call fails the test.
        _context.ChangeTracker.Clear();
        var second = await BuildSweep(Signed, callBudget: 0).FiscalisePendingSalesAsync(CancellationToken.None);
        Assert.Equal(0, second.Total);
    }

    [Fact]
    public async Task A_sale_that_has_run_out_of_attempts_is_left_for_a_human()
    {
        Seed("VEND-20260813-0004", attempts: new DesktopSalePostingSettings().MaxFiscalisationAttempts);
        await _context.SaveChangesAsync();

        var result = await BuildSweep(Signed, callBudget: 0).FiscalisePendingSalesAsync(CancellationToken.None);

        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task An_ordinary_failure_stays_pending_and_is_retried()
    {
        Seed("VEND-20260813-0005");
        await _context.SaveChangesAsync();

        await BuildSweep(() => new FiscalizationResult { Success = false, Message = "network" })
            .FiscalisePendingSalesAsync(CancellationToken.None);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleFiscalizationStatus.Failed, sale.FiscalizationStatus);
        Assert.False(sale.FiscalizationRequiresReconciliation);
        Assert.Equal(1, sale.FiscalizationAttempts);
    }

    [Fact]
    public async Task A_sale_that_has_already_been_signed_is_not_signed_again()
    {
        Seed("VEND-20260813-0006", status: DesktopSaleFiscalizationStatus.Success);
        Seed("VEND-20260813-0007", status: DesktopSaleFiscalizationStatus.Skipped);
        await _context.SaveChangesAsync();

        var result = await BuildSweep(Signed, callBudget: 0).FiscalisePendingSalesAsync(CancellationToken.None);

        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task A_van_sale_is_left_to_its_own_route()
    {
        // Van sales are signed on the handset, not by this sweep. Offering one to the platform here
        // would submit a receipt that already exists.
        Seed("VAN-20260813-0001", sourceSystem: SaleSourceSystems.VanSales);
        await _context.SaveChangesAsync();

        var result = await BuildSweep(Signed, callBudget: 0).FiscalisePendingSalesAsync(CancellationToken.None);

        Assert.Equal(0, result.Total);
    }

    [Fact]
    public void Only_vending_defers_its_fiscalisation()
    {
        // A shop till fiscalises in the request, because the receipt has to print before the customer
        // walks away. Deferring it would hand over an unsigned receipt.
        Assert.True(SaleSourceSystems.FiscalisesInBackground(SaleSourceSystems.Vending));
        Assert.False(SaleSourceSystems.FiscalisesInBackground(SaleSourceSystems.ShopTill));
        Assert.False(SaleSourceSystems.FiscalisesInBackground(SaleSourceSystems.VanSales));
        Assert.False(SaleSourceSystems.FiscalisesInBackground(null));
    }
}
