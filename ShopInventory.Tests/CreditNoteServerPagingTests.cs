using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNotes.Queries.GetAllCreditNotes;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Credit Notes page asks the API for one page, filtered and sorted, rather than for the whole date
/// range to hold and page itself.
/// </summary>
/// <remarks>
/// It fetched up to ten thousand notes per load and kept them for as long as the tab stayed open. The
/// number, customer and fiscal filters and the three sorts it applied in memory are applied here now,
/// with the same meaning, before the page is cut. The fiscal state is looked up, not stored, so a fiscal
/// filter looks up every match and an unfiltered page looks up only its own rows.
/// </remarks>
public sealed class CreditNoteServerPagingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<object?[]> _serviceCalls = [];

    public CreditNoteServerPagingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static readonly DateTime Day = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<CreditNoteDto> Range() =>
    [
        Note(101, "Avondale Spar", "C0001", Day.AddDays(3), 50m),
        Note(102, "Mbare Stores", "C0002", Day.AddDays(1), 900m),
        Note(103, "Zesa Canteen", "C0003", Day.AddDays(2), 50m),
        Note(104, "Mbare Stores", "C0002", Day, 10m),
        Note(110, "Avondale Spar", "C0001", Day.AddDays(4), 300m),
    ];

    [Fact]
    public async Task The_number_and_customer_filters_match_anywhere_ignoring_case()
    {
        var byNumber = await HandleAsync(new CreditNoteListOptions(CreditNoteNumber: "cn-10"));
        var byCustomer = await HandleAsync(new CreditNoteListOptions(Customer: "mbare"));
        var byCode = await HandleAsync(new CreditNoteListOptions(Customer: "c0003"));

        Assert.Equal([104, 103, 102, 101], DocNums(byNumber));
        Assert.Equal([104, 102], DocNums(byCustomer));
        Assert.Equal([103], DocNums(byCode));
        Assert.Equal(2, byCustomer.TotalCount);
    }

    [Theory]
    [InlineData(CreditNoteListSort.Number, true, new[] { 110, 104, 103, 102, 101 })]
    [InlineData(CreditNoteListSort.Number, false, new[] { 101, 102, 103, 104, 110 })]
    [InlineData(CreditNoteListSort.Date, true, new[] { 110, 101, 103, 102, 104 })]
    // Equal totals (101 and 103) fall back to the document entry, descending, whichever way the sort runs.
    [InlineData(CreditNoteListSort.Total, true, new[] { 102, 110, 103, 101, 104 })]
    [InlineData(CreditNoteListSort.Total, false, new[] { 104, 103, 101, 110, 102 })]
    public async Task Each_sort_orders_the_whole_range_before_it_is_paged(
        CreditNoteListSort sort, bool descending, int[] expected)
    {
        var pages = new List<int>();
        for (var page = 1; page <= 3; page++)
            pages.AddRange(DocNums(await HandleAsync(new CreditNoteListOptions(SortBy: sort, SortDescending: descending), page, pageSize: 2)));

        Assert.Equal(expected, pages);
    }

    [Fact]
    public async Task The_fiscal_filter_looks_up_every_match_before_paging()
    {
        await GivenFiscalRecord(101, qrCode: "QR-101");
        await GivenFiscalRecord(102, qrCode: null);

        var fiscalised = await HandleAsync(new CreditNoteListOptions(FiscalStatus: "Fiscalised"));
        var notFiscalised = await HandleAsync(new CreditNoteListOptions(FiscalStatus: "not fiscalised"));
        var unknown = await HandleAsync(new CreditNoteListOptions(FiscalStatus: "Unknown"), page: 1, pageSize: 2);

        Assert.Equal([101], DocNums(fiscalised));
        Assert.Equal([102], DocNums(notFiscalised));
        Assert.Equal([110, 104], DocNums(unknown));
        Assert.Equal(3, unknown.TotalCount);
    }

    [Fact]
    public async Task An_unfiltered_page_still_carries_its_fiscal_state()
    {
        await GivenFiscalRecord(110, qrCode: "QR-110");

        var page = await HandleAsync(new CreditNoteListOptions(), page: 1, pageSize: 1);

        var note = Assert.Single(page.CreditNotes);
        Assert.Equal(110, note.SAPDocNum);
        Assert.Equal("Fiscalised", note.FiscalizationStatus);
    }

    [Fact]
    public async Task The_whole_range_is_read_with_the_status_and_dates_and_without_lines()
    {
        await HandleAsync(new CreditNoteListOptions(), page: 3, pageSize: 25, status: CreditNoteStatus.Approved);

        var call = Assert.Single(_serviceCalls);
        Assert.Equal(1, call[0]);
        Assert.Equal(int.MaxValue, call[1]);
        Assert.Equal(CreditNoteStatus.Approved, call[2]);
        Assert.Equal(Day, call[4]);
        Assert.Equal(false, call[6]);
        Assert.Equal(false, call[7]);
    }

    [Fact]
    public async Task Without_list_options_the_list_answers_as_it_always_has()
    {
        var handler = CreateHandler();
        var result = await handler.Handle(new GetAllCreditNotesQuery(2, 20, null, null, Day, Day.AddDays(5)), CancellationToken.None);

        Assert.False(result.IsError);
        var call = Assert.Single(_serviceCalls);
        Assert.Equal(2, call[0]);
        Assert.Equal(20, call[1]);
    }

    private async Task<CreditNoteListResponseDto> HandleAsync(
        CreditNoteListOptions options, int page = 1, int pageSize = 50, CreditNoteStatus? status = null)
    {
        var result = await CreateHandler().Handle(
            new GetAllCreditNotesQuery(page, pageSize, status, null, Day, Day.AddDays(5), VanSalesOnly: false, ListOptions: options),
            CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    private GetAllCreditNotesHandler CreateHandler() => new(
        _context,
        StubProxy.For<ICreditNoteService>((method, args) =>
        {
            Assert.Equal(nameof(ICreditNoteService.GetAllAsync), method.Name);
            _serviceCalls.Add(args!);
            var notes = Range();
            return Task.FromResult(new CreditNoteListResponseDto
            {
                Page = 1,
                PageSize = (int)args![1]!,
                TotalCount = notes.Count,
                CreditNotes = notes
            });
        }));

    private static int[] DocNums(CreditNoteListResponseDto response) =>
        response.CreditNotes.Select(note => note.SAPDocNum!.Value).ToArray();

    private static CreditNoteDto Note(int docNum, string cardName, string cardCode, DateTime date, decimal total) => new()
    {
        Id = docNum + 1000,
        SAPDocEntry = docNum + 1000,
        SAPDocNum = docNum,
        CreditNoteNumber = $"SAP-CN-{docNum}",
        CreditNoteDate = date,
        CardCode = cardCode,
        CardName = cardName,
        DocTotal = total
    };

    private async Task GivenFiscalRecord(int docNum, string? qrCode)
    {
        _context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = $"cn-{docNum}",
            DocNum = docNum,
            DocumentType = "CreditNote",
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
