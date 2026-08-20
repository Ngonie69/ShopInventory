using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the level a failed SAP post is reported at, because level is what an operator alerts on.
/// </summary>
/// <remarks>
/// From a nine-hour production log: all nine <c>[ERR]</c> lines in the file were credit refusals —
/// a salesperson hitting a limit, rolled back correctly, logged with a full stack trace under
/// "because posting to SAP failed". Nothing had been posted; the gate runs before the SAP create.
/// The cost was not the noise but what it hid: an alert on <c>[ERR]</c> fires daily on normal
/// trading and gets muted, and the two genuinely broken things in that same log never reached the
/// level at all.
/// <para>
/// These tests exercise <see cref="SalesOrderService.LogFailedSapPost"/> directly. The catch blocks
/// that call it sit behind a Postgres advisory lock (<c>pg_try_advisory_lock</c>), so the approval
/// path itself cannot run on the SQLite fixture used here — the rule is what has to hold, and this
/// is where it lives.
/// </para>
/// </remarks>
public sealed class SalesOrderFailureLogLevelTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly CapturingLogger<SalesOrderService> _log = new();

    public SalesOrderFailureLogLevelTests()
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
    public void A_credit_refusal_is_not_reported_as_an_error()
    {
        var service = CreateService();

        service.LogFailedSapPost(
            new CreditLimitExceededException("This order would take Spar Avondale (SPA077) over its credit limit."),
            Order(),
            "approve",
            "Approval state was rolled back.");

        Assert.Empty(_log.AtOrAbove(LogLevel.Warning));

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);

        // No stack: the exception is the answer, not a fault to be diagnosed.
        Assert.Null(entry.Exception);

        // And it must not blame SAP for something SAP was never asked to do.
        Assert.DoesNotContain("posting to SAP failed", entry.Message);
        Assert.Contains("refused on credit", entry.Message);
        Assert.Contains("SO-20260820-0001", entry.Message);
        Assert.Contains("Approval state was rolled back.", entry.Message);
    }

    [Fact]
    public void A_genuine_posting_failure_is_still_reported_as_an_error()
    {
        var service = CreateService();
        var failure = new InvalidOperationException("SAP rejected the document.");

        service.LogFailedSapPost(failure, Order(), "post", "The order was returned to Pending.");

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);

        // The stack is the point for a real fault.
        Assert.Same(failure, entry.Exception);
        Assert.Contains("posting to SAP failed", entry.Message);
        Assert.Contains("post", entry.Message);
        Assert.Contains("The order was returned to Pending.", entry.Message);
    }

    /// <summary>
    /// A credit refusal derives from <see cref="InvalidOperationException"/>, which is also the type
    /// a rejected SAP document arrives as. The split has to be on the exact type, not on a base one.
    /// </summary>
    [Fact]
    public void The_split_is_on_the_credit_type_not_on_its_base_type()
    {
        Assert.IsAssignableFrom<InvalidOperationException>(new CreditLimitExceededException("over"));

        var service = CreateService();

        service.LogFailedSapPost(new CreditLimitExceededException("over"), Order(), "approve", "rolled back");
        service.LogFailedSapPost(new InvalidOperationException("broken"), Order(), "approve", "rolled back");

        Assert.Equal(
            [LogLevel.Information, LogLevel.Error],
            _log.Entries.Select(entry => entry.Level));
    }

    private static SalesOrderEntity Order() => new()
    {
        Id = 2862,
        OrderNumber = "SO-20260820-0001",
        CardCode = "SPA077"
    };

    private SalesOrderService CreateService() =>
        new(_context,
            StubProxy.Unused<ISAPServiceLayerClient>(),
            _log,
            new NoOpNotificationService(),
            StubProxy.Unused<IBusinessPartnerService>(),
            StubProxy.Unused<ILocalPriceCatalogService>(),
            StubProxy.Unused<IIdempotencyRequestStore>(),
            StubProxy.Unused<ICreditLimitService>(),
            Options.Create(new TaxSettings { VatRate = 0m }));
}
