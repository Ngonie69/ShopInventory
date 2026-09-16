using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Commands.RetryDesktopSaleFiscalisation;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the "Retry fiscalisation" lever on a sale whose fiscalisation failed.
///
/// Every retry is a submission to ZIMRA and a duplicate receipt cannot be withdrawn, so the assertions
/// that matter are that the device is always asked first, and that a sale which may not be retried is
/// refused before anything is sent.
/// </summary>
public sealed class RetryDesktopSaleFiscalisationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly Guid _adminId = Guid.NewGuid();

    private int _lookups;
    private int _submissions;

    public RetryDesktopSaleFiscalisationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = _adminId,
            Username = "admin",
            PasswordHash = "x",
            Role = ApplicationRoles.Admin,
            IsActive = true,
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_failed_till_sale_is_fiscalised_on_request_after_asking_the_device()
    {
        await SeedAsync("KEF-FAC-1", DesktopSaleFiscalizationStatus.Failed, attempts: 1);

        var result = await Retry("KEF-FAC-1", respond: Signed);

        Assert.False(result.IsError);
        Assert.Equal(DesktopSaleFiscalisationRetryOutcomes.Fiscalised, result.Value.Outcome);
        Assert.Equal("216501", result.Value.FiscalReceiptNumber);
        Assert.Equal(1, _lookups);
        Assert.Equal(1, _submissions);

        var sale = await _context.DesktopSales.AsNoTracking().SingleAsync();
        Assert.Equal(DesktopSaleFiscalizationStatus.Success, sale.FiscalizationStatus);
        Assert.Equal(2, sale.FiscalizationAttempts);
    }

    [Fact]
    public async Task A_receipt_the_device_already_holds_is_adopted_and_nothing_is_sent()
    {
        await SeedAsync("KEF-FAC-2", DesktopSaleFiscalizationStatus.Failed, attempts: 1);

        var result = await Retry(
            "KEF-FAC-2",
            respond: Signed,
            existing: () => new FiscalizationResult { Success = true, ReceiptGlobalNo = "216400", QRCode = "qr" });

        Assert.False(result.IsError);
        Assert.Equal(DesktopSaleFiscalisationRetryOutcomes.AlreadyFiscalised, result.Value.Outcome);
        Assert.Equal(0, _submissions);
    }

    [Fact]
    public async Task Nothing_is_sent_or_saved_when_the_device_cannot_be_asked()
    {
        await SeedAsync("KEF-FAC-3", DesktopSaleFiscalizationStatus.Failed, attempts: 1);

        var result = await Retry(
            "KEF-FAC-3",
            respond: Signed,
            existing: () => throw new InvalidOperationException("REVMax is unreachable"));

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SaleFiscalisationUncheckable", result.FirstError.Code);
        Assert.Equal(0, _submissions);

        var sale = await _context.DesktopSales.AsNoTracking().SingleAsync();
        Assert.Equal(DesktopSaleFiscalizationStatus.Failed, sale.FiscalizationStatus);
        Assert.Equal(1, sale.FiscalizationAttempts);
    }

    [Fact]
    public async Task A_refusal_from_the_device_is_reported_and_recorded()
    {
        await SeedAsync("KEF-FAC-4", DesktopSaleFiscalizationStatus.Failed, attempts: 1);

        var result = await Retry(
            "KEF-FAC-4",
            respond: () => new FiscalizationResult { Success = false, Message = "Invalid ITEMNAME2" });

        Assert.True(result.IsError);
        Assert.Contains("Invalid ITEMNAME2", result.FirstError.Description);

        var sale = await _context.DesktopSales.AsNoTracking().SingleAsync();
        Assert.Equal(DesktopSaleFiscalizationStatus.Failed, sale.FiscalizationStatus);
        Assert.Equal("Invalid ITEMNAME2", sale.FiscalError);
    }

    [Fact]
    public async Task The_attempt_budget_does_not_stop_a_person()
    {
        await SeedAsync("KEF-FAC-5", DesktopSaleFiscalizationStatus.Failed, attempts: 50);

        var result = await Retry("KEF-FAC-5", respond: Signed);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task A_sale_that_already_has_a_receipt_is_refused_before_anything_is_sent()
    {
        await SeedAsync("KEF-FAC-6", DesktopSaleFiscalizationStatus.Success);

        var result = await Retry("KEF-FAC-6", respond: Signed);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SaleNotFiscalisable", result.FirstError.Code);
        Assert.Equal(0, _lookups + _submissions);
    }

    [Fact]
    public async Task A_sale_created_with_fiscalisation_switched_off_may_be_signed_on_request()
    {
        // Skipped used to be refused here, with "fiscalisation was not asked for when this sale was
        // made" — true, and not a reason. It became one the moment the posting services stopped
        // admitting a Skipped sale to SAP: the sale then has no receipt, no invoice, and no way to
        // acquire either, and refusing the one control that could still fix it strands the money.
        await SeedAsync("KEF-FAC-6b", DesktopSaleFiscalizationStatus.Skipped);

        var result = await Retry("KEF-FAC-6b", respond: Signed);

        Assert.False(result.IsError);

        var sale = await _context.DesktopSales.AsNoTracking()
            .SingleAsync(row => row.ExternalReferenceId == "KEF-FAC-6b");
        Assert.Equal(DesktopSaleFiscalizationStatus.Success, sale.FiscalizationStatus);
    }

    [Fact]
    public async Task A_till_sale_its_own_request_is_still_fiscalising_is_refused()
    {
        await SeedAsync("KEF-FAC-7", DesktopSaleFiscalizationStatus.Pending, createdAt: DateTime.UtcNow);

        var result = await Retry("KEF-FAC-7", respond: Signed);

        Assert.True(result.IsError);
        Assert.Equal(0, _lookups + _submissions);
    }

    [Fact]
    public async Task An_unresolved_sale_is_retried_under_revmax_but_not_under_the_platform()
    {
        // REVMax's look-up reads the device itself, so asking it is the reconciliation. The platform's
        // reads an archive that can lag, so there a person still has to look.
        await SeedAsync("KEF-FAC-8", DesktopSaleFiscalizationStatus.Failed, attempts: 1, requiresReconciliation: true);

        var refused = await Retry("KEF-FAC-8", respond: Signed, provider: FiscalisationProvider.Platform);
        Assert.True(refused.IsError);
        Assert.Equal(0, _lookups + _submissions);

        _context.ChangeTracker.Clear();
        var retried = await Retry("KEF-FAC-8", respond: Signed);
        Assert.False(retried.IsError);

        var sale = await _context.DesktopSales.AsNoTracking().SingleAsync();
        Assert.False(sale.FiscalizationRequiresReconciliation);
    }

    [Fact]
    public async Task An_online_van_sale_row_is_refused()
    {
        await SeedAsync("VAN-ONLINE-1", DesktopSaleFiscalizationStatus.Failed, source: SaleSourceSystems.VanSalesOnline);

        var result = await Retry("VAN-ONLINE-1", respond: Signed);

        Assert.True(result.IsError);
        Assert.Equal(0, _lookups + _submissions);
    }

    private static FiscalizationResult Signed() => new()
    {
        Success = true,
        ReceiptGlobalNo = "216501",
        QRCode = "qr",
        VerificationCode = "vc",
        DeviceSerial = "8DE6996C0188",
    };

    private async Task SeedAsync(
        string reference,
        DesktopSaleFiscalizationStatus status,
        int attempts = 0,
        bool requiresReconciliation = false,
        string source = SaleSourceSystems.ShopTill,
        DateTime? createdAt = null)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = source,
            CardCode = "KEFSHOP-BP",
            WarehouseCode = "KEFSHOP",
            DocDate = DateTime.UtcNow.Date,
            Currency = "USD",
            TotalAmount = 18.87m,
            VatAmount = 2.53m,
            AmountPaid = 18.87m,
            PaymentMethod = TenderTypes.Cash,
            FiscalizationStatus = status,
            FiscalizationAttempts = attempts,
            FiscalizationRequiresReconciliation = requiresReconciliation,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            CreatedAt = createdAt ?? DateTime.UtcNow.AddHours(-1),
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private Task<ErrorOr.ErrorOr<DesktopSaleFiscalisationRetryResult>> Retry(
        string reference,
        Func<FiscalizationResult> respond,
        Func<FiscalizationResult?>? existing = null,
        FiscalisationProvider provider = FiscalisationProvider.Revmax)
    {
        var fiscalisation = StubProxy.For<IFiscalizationService>((method, _) => method.Name switch
        {
            nameof(IFiscalizationService.FindPreSapReceiptAsync) =>
                (object)Task.FromResult<FiscalizationResult?>(Lookup(existing)),
            nameof(IFiscalizationService.FiscalizePreSapInvoiceAsync) => Task.FromResult(Submit(respond)),
            _ => throw new InvalidOperationException($"IFiscalizationService.{method.Name} was not expected.")
        });

        var notifications = StubProxy.For<INotificationService>((method, _) => method.Name switch
        {
            nameof(INotificationService.CreateNotificationAsync) => Task.FromResult(0),
            _ => throw new InvalidOperationException($"INotificationService.{method.Name} was not expected.")
        });

        var fiscaliser = new DesktopSaleFiscaliser(
            fiscalisation, notifications, Options.Create(new TaxSettings()), NullLogger<DesktopSaleFiscaliser>.Instance);

        return new RetryDesktopSaleFiscalisationHandler(
                _context,
                fiscaliser,
                Options.Create(new FiscalisationSettings { Provider = provider }),
                new RecordingAuditService(),
                NullLogger<RetryDesktopSaleFiscalisationHandler>.Instance)
            .Handle(new RetryDesktopSaleFiscalisationCommand(_adminId, reference), CancellationToken.None);
    }

    private FiscalizationResult? Lookup(Func<FiscalizationResult?>? existing)
    {
        _lookups++;
        return existing?.Invoke();
    }

    private FiscalizationResult Submit(Func<FiscalizationResult> respond)
    {
        _submissions++;
        return respond();
    }
}
