using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Queries.GetPagedInvoices;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Invoices page asks for one page; SAP filters, counts, totals and pages it, and the fiscal filter
/// is answered from a short-lived scan here.
/// </summary>
/// <remarks>
/// The page loaded up to 5,000 invoices (100 unfiltered) into every open tab and filtered them by fiscal
/// state, searched them, totalled the tiles and paged them itself. SAP holds no fiscal state, so a fiscal
/// filter reads the matches once, looks up their fiscal state, and serves the following pages from that
/// scan for two minutes instead of reading SAP again for each one.
/// </remarks>
public sealed class InvoiceServerPagingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly List<(string Method, object?[] Args)> _sapCalls = [];
    private List<Invoice> _sapInvoices = [];

    public InvoiceServerPagingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _cache.Dispose();
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Without_a_fiscal_filter_SAP_searches_counts_totals_and_pages()
    {
        _sapInvoices = Invoices(30);

        var result = await HandleAsync(new GetPagedInvoicesQuery(2, 10, null, "C001", null, null, Search: "spar", IncludeSummary: true));

        var page = Assert.Single(_sapCalls, call => call.Method == nameof(ISAPServiceLayerClient.GetPagedInvoicesByOffsetAsync));
        Assert.Equal(10, page.Args[0]);
        Assert.Equal(10, page.Args[1]);
        Assert.Equal("C001", page.Args[3]);
        Assert.Equal("spar", page.Args[9]);
        var count = Assert.Single(_sapCalls, call => call.Method == nameof(ISAPServiceLayerClient.GetInvoicesCountAsync));
        Assert.Equal("spar", count.Args[6]);
        Assert.Single(_sapCalls, call => call.Method == nameof(ISAPServiceLayerClient.SummarizeInvoicesAsync));

        Assert.Equal(30, result.TotalCount);
        Assert.Equal(new InvoiceListTotals(30, 3000m, 300m, 3), (result.Summary!.Count, result.Summary.Total, result.Summary.Vat, result.Summary.Customers) switch
        {
            var (c, t, v, cu) => new InvoiceListTotals(c, t, v, cu)
        });
        Assert.Null(result.Summary.FiscalisableCount);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(201, true)]
    public async Task An_unfiltered_page_may_be_up_to_200_rows(int pageSize, bool refused)
    {
        _sapInvoices = Invoices(5);
        var result = await CreateHandler().Handle(new GetPagedInvoicesQuery(1, pageSize, null, null, null, null), CancellationToken.None);
        Assert.Equal(refused, result.IsError);
    }

    [Fact]
    public async Task The_fiscal_filter_scans_once_and_pages_from_the_scan()
    {
        _sapInvoices = Invoices(25);
        for (var docNum = 1; docNum <= 25; docNum += 2)
            await GivenFiscalRecord(docNum, qrCode: $"QR-{docNum}");

        var first = await HandleAsync(new GetPagedInvoicesQuery(1, 5, null, null, null, null, FiscalStatus: "Unknown", IncludeSummary: true));
        var second = await HandleAsync(new GetPagedInvoicesQuery(2, 5, null, null, null, null, FiscalStatus: "Unknown"));
        var fiscalised = await HandleAsync(new GetPagedInvoicesQuery(1, 5, null, null, null, null, FiscalStatus: "fiscalised"));

        var scans = _sapCalls.Where(call => call.Method == nameof(ISAPServiceLayerClient.GetPagedInvoicesByOffsetAsync)).ToList();
        var scan = Assert.Single(scans);
        Assert.Equal(0, scan.Args[0]);
        Assert.Equal(GetPagedInvoicesHandler.ScanLimit, scan.Args[1]);

        // Even doc numbers have no fiscal record; newest (highest DocEntry) first.
        Assert.Equal(12, first.TotalCount);
        Assert.Equal([24, 22, 20, 18, 16], first.Invoices!.Select(invoice => invoice.DocNum));
        Assert.Equal([14, 12, 10, 8, 6], second.Invoices!.Select(invoice => invoice.DocNum));
        Assert.Equal(13, fiscalised.TotalCount);
        Assert.All(fiscalised.Invoices!, invoice => Assert.Equal("Fiscalised", invoice.FiscalizationStatus));

        var summary = first.Summary!;
        Assert.Equal(12, summary.Count);
        Assert.Equal(12 * 100m, summary.Total);
        Assert.Equal(12, summary.FiscalisableCount);
        Assert.False(first.ScanLimitReached);
    }

    [Fact]
    public async Task A_refresh_reads_SAP_again()
    {
        _sapInvoices = Invoices(3);

        await HandleAsync(new GetPagedInvoicesQuery(1, 5, null, null, null, null, FiscalStatus: "Unknown"));
        await HandleAsync(new GetPagedInvoicesQuery(1, 5, null, null, null, null, FiscalStatus: "Unknown", RefreshScan: true));
        await HandleAsync(new GetPagedInvoicesQuery(1, 5, null, "C002", null, null, FiscalStatus: "Unknown"));

        Assert.Equal(3, _sapCalls.Count(call => call.Method == nameof(ISAPServiceLayerClient.GetPagedInvoicesByOffsetAsync)));
    }

    [Fact]
    public async Task Fiscalisable_only_answers_every_match_still_to_fiscalise()
    {
        _sapInvoices = Invoices(8);
        await GivenFiscalRecord(8, qrCode: "QR-8");
        await GivenFiscalRecord(7, qrCode: null);

        var result = await HandleAsync(new GetPagedInvoicesQuery(1, GetPagedInvoicesHandler.ScanLimit, null, null, null, null, FiscalisableOnly: true));

        Assert.Equal([7, 6, 5, 4, 3, 2, 1], result.Invoices!.Select(invoice => invoice.DocNum));
        Assert.Equal(7, result.TotalCount);
    }

    [Fact]
    public async Task A_scan_that_reaches_its_limit_says_so()
    {
        _sapInvoices = Invoices(GetPagedInvoicesHandler.ScanLimit);

        var result = await HandleAsync(new GetPagedInvoicesQuery(1, 10, null, null, null, null, FiscalStatus: "Unknown"));

        Assert.True(result.ScanLimitReached);
    }

    private async Task<InvoiceListResponseDto> HandleAsync(GetPagedInvoicesQuery query)
    {
        var result = await CreateHandler().Handle(query, CancellationToken.None);
        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private GetPagedInvoicesHandler CreateHandler() => new(
        _context,
        StubProxy.For<ISAPServiceLayerClient>((method, args) =>
        {
            _sapCalls.Add((method.Name, args!));
            return method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetPagedInvoicesByOffsetAsync) => Task.FromResult(
                    _sapInvoices.OrderByDescending(invoice => invoice.DocEntry).Skip((int)args![0]!).Take((int)args[1]!).ToList()),
                nameof(ISAPServiceLayerClient.GetInvoicesCountAsync) => Task.FromResult(_sapInvoices.Count),
                nameof(ISAPServiceLayerClient.SummarizeInvoicesAsync) => Task.FromResult(new InvoiceListTotals(
                    _sapInvoices.Count, _sapInvoices.Sum(i => i.DocTotal), _sapInvoices.Sum(i => i.VatSum),
                    _sapInvoices.Select(i => i.CardCode).Distinct().Count())),
                _ => throw new InvalidOperationException($"{method.Name} was not expected")
            };
        }),
        StubProxy.Unused<IInvoiceFiscalStatusBackfillQueue>(),
        _cache,
        Options.Create(new SAPSettings { Enabled = true }),
        Options.Create(new FiscalisationSettings { Enabled = false }),
        NullLogger<GetPagedInvoicesHandler>.Instance);

    private static List<Invoice> Invoices(int count) => Enumerable.Range(1, count).Select(i => new Invoice
    {
        DocEntry = i,
        DocNum = i,
        CardCode = $"C00{i % 3}",
        CardName = "Customer",
        DocTotal = 100m,
        VatSum = 10m,
        DocCurrency = "USD",
        DocDate = "2026-10-01"
    }).ToList();

    private async Task GivenFiscalRecord(int docNum, string? qrCode)
    {
        _context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = $"inv-{docNum}",
            DocNum = docNum,
            DocumentType = "Invoice",
            Status = qrCode is null ? "Not Fiscalised" : "Fiscalised",
            QRCode = qrCode,
            TimestampUtc = DateTime.UtcNow,
            LastSyncedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
