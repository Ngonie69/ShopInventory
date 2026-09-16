using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.Invoices.Queries.ValidateBulkPods;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins what the sales-order POD check does once SAP stops answering part-way through a request.
/// </summary>
/// <remarks>
/// A request spanning several doc-number ranges used to try every range in turn, each paying its own
/// retries, when they all run the same statement against the same SAP.
/// </remarks>
public sealed class ValidateBulkPodsSapFailureTests : IDisposable
{
    // 1500 and 5500 fall in different 1000-wide buckets, so they cost two SAP queries.
    private static readonly List<int> TwoRanges = [1500, 5500];

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ValidateBulkPodsSapFailureTests()
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
    public async Task Ranges_after_a_failed_one_are_not_sent_to_SAP()
    {
        var sqlCalls = 0;
        var handler = CreateHandler(() =>
        {
            sqlCalls++;
            throw new SapCircuitOpenException("SAP circuit breaker is open. Retry after 27 seconds.", TimeSpan.FromSeconds(27));
        });

        var result = await handler.Handle(new ValidateBulkPodsQuery([], TwoRanges), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, sqlCalls);
        Assert.All(result.Value.Results, row =>
        {
            Assert.False(row.Found);
            Assert.True(row.LookupFailed);
        });
        Assert.Contains("skipped", result.Value.Results.Single(row => row.SalesOrderDocNum == 5500).ErrorMessage);
    }

    [Fact]
    public async Task Every_range_is_asked_when_SAP_answers()
    {
        var sqlCalls = 0;
        var handler = CreateHandler(() =>
        {
            sqlCalls++;
            return [];
        });

        var result = await handler.Handle(new ValidateBulkPodsQuery([], TwoRanges), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(2, sqlCalls);
        // Answered, with no invoice: a clean "not found", which is not a failed lookup.
        Assert.All(result.Value.Results, row => Assert.False(row.LookupFailed));
    }

    private ValidateBulkPodsHandler CreateHandler(Func<List<Dictionary<string, object?>>> runSql)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) =>
            method.Name == nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryAsync)
                ? Task.Run(runSql)
                : null);

        var documents = StubProxy.For<IDocumentService>((method, _) =>
            method.Name == nameof(IDocumentService.GetPodStatusByDocEntriesAsync)
                ? Task.FromResult(new Dictionary<int, PodStatusInfo>())
                : null);

        return new ValidateBulkPodsHandler(
            _context,
            sap,
            documents,
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<ValidateBulkPodsHandler>.Instance);
    }
}
