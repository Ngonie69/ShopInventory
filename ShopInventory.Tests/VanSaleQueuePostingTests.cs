using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A converted van order reaches SAP.
///
/// It used not to. The queue job fiscalised it and parked it at Fiscalized for end-of-day consolidation,
/// and consolidation refuses van sales by design — so every converted order was a receipt in a customer's
/// hand with no invoice in SAP, and nothing ever looked at it again. These run the real job over the real
/// queue, with only the device and SAP stood in for.
/// </summary>
public sealed class VanSaleQueuePostingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly List<string> _calls = [];

    public VanSaleQueuePostingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var fiscalisation = StubProxy.For<IFiscalizationService>((method, _) => method.Name switch
        {
            // The stranded entries were signed long ago, under the same reference.
            nameof(IFiscalizationService.FindPreSapReceiptAsync) => (object)Task.FromResult<FiscalizationResult?>(
                Record("lookup", new FiscalizationResult
                {
                    Success = true,
                    AlreadyFiscalised = true,
                    ReceiptGlobalNo = "512",
                    VerificationCode = "vc-512",
                    DeviceSerial = "DEV-1"
                })),

            nameof(IFiscalizationService.FiscalizePreSapInvoiceAsync) =>
                throw new InvalidOperationException("A stranded entry was signed a second time."),

            _ => throw new InvalidOperationException($"IFiscalizationService.{method.Name} was not expected.")
        });

        var reservations = StubProxy.For<IStockReservationService>((method, _) => method.Name switch
        {
            nameof(IStockReservationService.ConfirmReservationAsync) => Task.FromResult(Record(
                "post",
                new ConfirmReservationResponseDto { Success = true, SAPDocEntry = 77, SAPDocNum = 4077 })),

            _ => throw new InvalidOperationException($"IStockReservationService.{method.Name} was not expected.")
        });

        _services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection))
            .AddSingleton(Options.Create(new TaxSettings()))
            .AddSingleton(fiscalisation)
            .AddSingleton(reservations)
            .AddSingleton(StubProxy.For<INotificationService>((method, _) => Task.FromResult(0)))
            .AddSingleton(StubProxy.Unused<IStockLedger>())
            .AddScoped<IInvoiceQueueService, InvoiceQueueService>()
            .AddScoped<DesktopSaleFiscaliser>()
            .AddScoped<VanSaleFiscalFirstPoster>()
            .BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private T Record<T>(string call, T value)
    {
        _calls.Add(call);
        return value;
    }

    private void SeedQueued(string reference, string sourceSystem, InvoiceQueueStatus status)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var reservation = new StockReservationEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = sourceSystem,
            CardCode = "C-100",
            CardName = "Chitungwiza Wholesalers",
            Currency = "USD",
            Status = ReservationStatus.Pending,
            // Long past its hour: the queue entry is what has kept it holding.
            ExpiresAt = DateTime.UtcNow.AddDays(-3),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0, ItemCode = "CHS001", OriginalQuantity = 4, ReservedQuantity = 4,
                    UnitPrice = 10m, LineTotal = 40m, WarehouseCode = "VAN008"
                }
            ]
        };

        db.StockReservations.Add(reservation);
        db.InvoiceQueue.Add(new InvoiceQueueEntity
        {
            ReservationId = reservation.ReservationId,
            ExternalReference = reference,
            CustomerCode = "C-100",
            InvoicePayload = "{\"notes\":\"Van sales conversion from SO-88\"}",
            Status = status,
            SourceSystem = sourceSystem,
            CreatedAt = DateTime.UtcNow.AddDays(-3),
            RequiresFiscalization = true
        });
        db.SaveChanges();
    }

    private async Task RunJobAsync()
    {
        var context = StubProxy.For<IJobExecutionContext>((method, _) => method.Name switch
        {
            "get_CancellationToken" => CancellationToken.None,
            _ => throw new InvalidOperationException($"IJobExecutionContext.{method.Name} was not expected.")
        });

        await new InvoicePostingJob(_services, NullLogger<InvoicePostingJob>.Instance).Execute(context);
    }

    private InvoiceQueueEntity Entry(string reference)
    {
        using var scope = _services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .InvoiceQueue.AsNoTracking().Single(q => q.ExternalReference == reference);
    }

    [Fact]
    public async Task A_van_sale_stranded_at_Fiscalized_is_invoiced_under_the_receipt_it_already_holds()
    {
        SeedQueued("VAN-CONV-1", SaleSourceSystems.VanSales, InvoiceQueueStatus.Fiscalized);

        await RunJobAsync();

        Assert.Equal(["lookup", "post"], _calls);

        var entry = Entry("VAN-CONV-1");
        Assert.Equal(InvoiceQueueStatus.Completed, entry.Status);
        Assert.Equal(4077, entry.SapDocNum);
        Assert.Equal("512", entry.FiscalReceiptNumber);

        // And a credit note against DocNum 4077 can now find the receipt it has to cite.
        using var scope = _services.CreateScope();
        var sale = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .DesktopSales.AsNoTracking().Single(s => s.ExternalReferenceId == "VAN-CONV-1");
        Assert.Equal(4077, sale.SapDocNum);
        Assert.Equal(DesktopSaleFiscalizationStatus.Success, sale.FiscalizationStatus);
    }

    [Fact]
    public async Task A_desktop_invoice_at_Fiscalized_is_still_left_for_consolidation()
    {
        SeedQueued("DESK-1", "DESKTOP_APP", InvoiceQueueStatus.Fiscalized);

        await RunJobAsync();

        Assert.Empty(_calls);
        Assert.Equal(InvoiceQueueStatus.Fiscalized, Entry("DESK-1").Status);

        using var scope = _services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IInvoiceQueueService>();
        Assert.Equal(["DESK-1"], (await queue.GetFiscalizedInvoicesAsync()).Select(q => q.ExternalReference));
    }
}
