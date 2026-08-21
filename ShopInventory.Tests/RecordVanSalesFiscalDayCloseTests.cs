using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility.Commands.RecordVanSalesFiscalDayClose;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the only route by which a van's fiscal day can be closed.
///
/// The platform holds a handset's certificate and not its private key, so it can verify the close the
/// handset signed but never produce one. If this never arrives, the day stays open and ZIMRA is never
/// told what it sold — so what this handler refuses, and what it lets through untouched, both matter.
/// </summary>
public sealed class RecordVanSalesFiscalDayCloseTests : IDisposable
{
    private const int DeviceId = 36189;
    private const int FiscalDayNo = 12;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public RecordVanSalesFiscalDayCloseTests()
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
    public async Task Holds_a_signed_close_against_its_day()
    {
        AddDayState();
        await _context.SaveChangesAsync();

        var result = await Handle(Request());

        Assert.False(result.IsError);
        Assert.True(result.Value.accepted);
        Assert.False(result.Value.duplicate);

        var state = await ReadStateAsync();
        Assert.NotNull(state.DeclaredCloseJson);
        Assert.NotNull(state.DeclaredCloseReceivedAtUtc);
    }

    /// <summary>
    /// The stored payload is the platform's shape, already mapped. It crosses this service once, because
    /// every crossing is a chance to alter a value the device's signature covers.
    /// </summary>
    [Fact]
    public async Task Stores_the_close_in_the_shape_the_platform_receives()
    {
        AddDayState();
        await _context.SaveChangesAsync();

        await Handle(Request());

        var stored = JsonSerializer.Deserialize<DeclaredFiscalDayCloseApiRequest>(
            (await ReadStateAsync()).DeclaredCloseJson!);

        Assert.Equal("c2lnbmF0dXJl", stored!.SignatureValue);
        var counter = Assert.Single(stored.Counters);
        Assert.Equal("SaleByTax", counter.FiscalCounterType);
        Assert.Equal("USD", counter.FiscalCounterCurrency);
        Assert.Equal(15.00m, counter.FiscalCounterTaxPercent);
        Assert.Equal(200.00m, counter.FiscalCounterValue);
    }

    /// <summary>
    /// Null and zero are different counters to FDMS — null is untaxed, zero is zero-rated — and the
    /// device signed whichever it sent. Filling one in would produce a close the platform refuses.
    /// </summary>
    [Fact]
    public async Task Keeps_an_absent_tax_percentage_absent()
    {
        AddDayState();
        await _context.SaveChangesAsync();

        var request = Request();
        request.counters[0].tax_percent = null;
        request.counters[0].money_type = "Cash";

        await Handle(request);

        var stored = JsonSerializer.Deserialize<DeclaredFiscalDayCloseApiRequest>(
            (await ReadStateAsync()).DeclaredCloseJson!);

        Assert.Null(Assert.Single(stored!.Counters).FiscalCounterTaxPercent);
        Assert.Equal("Cash", stored.Counters[0].FiscalCounterMoneyType);
    }

    /// <summary>
    /// A handset that loses the response re-sends, so re-arrival is routine. The held close is kept rather
    /// than overwritten — replacing one after the day was packaged would swap a close already in flight.
    /// </summary>
    [Fact]
    public async Task Treats_a_resend_as_a_duplicate_and_keeps_the_held_close()
    {
        AddDayState();
        await _context.SaveChangesAsync();

        await Handle(Request());
        var first = (await ReadStateAsync()).DeclaredCloseJson;

        var resend = Request();
        resend.counters[0].value = 999.00m;
        var result = await Handle(resend);

        Assert.True(result.Value.duplicate);
        Assert.Equal(first, (await ReadStateAsync()).DeclaredCloseJson);
    }

    /// <summary>
    /// An empty declaration asserts the day traded nothing. A handset that failed to load its receipts
    /// would send exactly that, and the platform would refuse the day for totals that disagree with the
    /// receipts it holds.
    /// </summary>
    [Fact]
    public async Task Refuses_a_close_declaring_no_counters()
    {
        AddDayState();
        await _context.SaveChangesAsync();

        var request = Request();
        request.counters.Clear();

        var result = await Handle(request);

        Assert.True(result.IsError);
        Assert.Contains("traded nothing", result.Errors[0].Description);
    }

    /// <summary>The whole point of the payload is the signature; without it there is nothing to forward.</summary>
    [Fact]
    public async Task Refuses_a_close_with_no_signature()
    {
        AddDayState();
        await _context.SaveChangesAsync();

        var request = Request();
        request.signature_value = null;

        var result = await Handle(request);

        Assert.True(result.IsError);
        Assert.Contains("device signature", result.Errors[0].Description);
    }

    /// <summary>
    /// A close arriving before any receipt of its day is out of order rather than wrong: the day's state
    /// row is created when receipts first appear. The handset should re-send once its backlog drains.
    /// </summary>
    [Fact]
    public async Task Refuses_a_close_for_a_day_it_has_never_seen()
    {
        var result = await Handle(Request());

        Assert.True(result.IsError);
        Assert.Contains("not yet known", result.Errors[0].Description);
    }

    [Fact]
    public async Task Refuses_a_close_naming_no_device_or_day()
    {
        var request = Request();
        request.device_id = 0;

        var result = await Handle(request);

        Assert.True(result.IsError);
        Assert.Contains("name the device", result.Errors[0].Description);
    }

    private async Task<ErrorOr.ErrorOr<VanSalesFiscalDayCloseResponse>> Handle(
        VanSalesFiscalDayCloseRequest request)
        => await new RecordVanSalesFiscalDayCloseHandler(
                _context,
                NullLogger<RecordVanSalesFiscalDayCloseHandler>.Instance)
            .Handle(new RecordVanSalesFiscalDayCloseCommand(request, Guid.NewGuid()), CancellationToken.None);

    private void AddDayState() => _context.FiscalDayStates.Add(new FiscalDayStateEntity
    {
        DeviceId = DeviceId,
        FiscalDayNo = FiscalDayNo,
        Status = FiscalDayLifecycleStatus.Open
    });

    private Task<FiscalDayStateEntity> ReadStateAsync()
        => _context.FiscalDayStates
            .AsNoTracking()
            .SingleAsync(state => state.DeviceId == DeviceId && state.FiscalDayNo == FiscalDayNo);

    private static VanSalesFiscalDayCloseRequest Request() => new()
    {
        device_id = DeviceId,
        fiscal_day_no = FiscalDayNo,
        fiscal_day_opened_at = "2026-08-19T06:00:00",
        signature_hash = "aGFzaA==",
        signature_value = "c2lnbmF0dXJl",
        counters =
        [
            new VanSalesFiscalDayCounterDto
            {
                counter_type = "SaleByTax",
                currency = "USD",
                tax_id = 1,
                tax_percent = 15.00m,
                value = 200.00m
            }
        ]
    };
}
