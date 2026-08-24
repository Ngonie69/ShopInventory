using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesOrderHistory;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Which SAP invoices a van sales handset is shown.
/// </summary>
/// <remarks>
/// <para>The screen is titled "Invoices" and a rep reads it to answer a shop asking about a document
/// it is holding. It used to answer a narrower question than that — the invoices <em>this account</em>
/// had itself fiscalised — because the SAP headers were inner-joined onto this rep's own rows in
/// <c>DesktopFiscalTransactions</c>.</para>
///
/// <para>That join very nearly emptied the screen, and the reason is worth stating because nothing
/// about it was visible. Every van sale is signed on the handset and uploaded; the invoice is cut at
/// end of day by <c>ConsolidateDailySalesHandler</c>, whose fiscal transaction is recorded through
/// <c>InvoiceFiscalTransactionSync.RecordConsolidatedInvoiceAsync</c> — and that passes no user, so
/// the row lands with a null <c>CreatedByUserId</c> and matched nobody. Only the server-fiscalised
/// fallback, which is the exception, ever appeared. <see cref="An_invoice_with_no_fiscal_transaction_is_still_listed"/>
/// is the guard for that shape specifically.</para>
///
/// <para>The other half is the one that has to hold while the first is relaxed. What replaced the
/// join as the narrowing is the customer scope, and it is now the <em>only</em> narrowing — so a
/// scope that resolves to nothing must show nothing rather than everything, and the code SAP is
/// asked for has to come from the account rather than from the request.</para>
/// </remarks>
public sealed class VanSalesInvoiceHistoryScopeTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OtherRep = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>The business partner the van is assigned to. Every shop on its route bills here.</summary>
    private const string VanBusinessPartner = "C-VAN-014";

    private const string WindowStart = "2026-08-10";
    private const string WindowEnd = "2026-08-24";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesInvoiceHistoryScopeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SqliteApplicationDbContext(
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

    // ── The regression: an invoice nobody on this handset fiscalised ────────

    [Fact]
    public async Task An_invoice_with_no_fiscal_transaction_is_still_listed()
    {
        // The consolidated van sale. Signed on a handset days ago, cut into a SAP invoice at end of
        // day, and stamped with no user at all — which is precisely the row the old join dropped.
        GivenVanRep();

        var history = await WhenHistoryIsRead(SapHolds(Invoice(docEntry: 9001, docNum: 4021)));

        var invoice = Assert.Single(history);
        Assert.Equal("4021", invoice.DocNum);

        // Listed, and honestly labelled. There is no fiscal row behind it, so the screen must not
        // claim one — "Not Fiscalised" is a true statement about what this server knows.
        Assert.Equal(0, invoice.Fiscalized);
        Assert.Equal("Not Fiscalised", invoice.FiscalizedText);
    }

    [Fact]
    public async Task An_invoice_fiscalised_by_another_account_carries_its_own_verification()
    {
        // Raised at the depot against the same business partner. The rep did not make it and could
        // not have; they are being shown it because the shop it names is theirs to answer for.
        GivenVanRep();
        GivenFiscalTransaction(docNum: 4022, createdBy: OtherRep.ToString(), verification: "DEPOT-77");

        var history = await WhenHistoryIsRead(SapHolds(Invoice(docEntry: 9002, docNum: 4022)));

        var invoice = Assert.Single(history);
        Assert.Equal(1, invoice.Fiscalized);
        Assert.Equal("Fiscalised", invoice.FiscalizedText);

        // The document's verification code, not the reader's. It belongs to the invoice.
        Assert.Equal("DEPOT-77", invoice.Verification);
    }

    [Fact]
    public async Task A_document_fiscalised_twice_reports_its_latest_attempt()
    {
        // A retry leaves two rows against one DocNum. The later one is what actually stands with
        // ZIMRA, so it is the code a rep would read out over the phone.
        GivenVanRep();
        GivenFiscalTransaction(
            docNum: 4023,
            createdBy: null,
            verification: "FIRST-TRY",
            at: new DateTime(2026, 8, 20, 6, 0, 0, DateTimeKind.Utc));
        GivenFiscalTransaction(
            docNum: 4023,
            createdBy: null,
            verification: "SECOND-TRY",
            at: new DateTime(2026, 8, 20, 9, 30, 0, DateTimeKind.Utc));

        var history = await WhenHistoryIsRead(SapHolds(Invoice(docEntry: 9003, docNum: 4023)));

        Assert.Equal("SECOND-TRY", Assert.Single(history).Verification);
    }

    // ── The narrowing that is now the only one ──────────────────────────────

    [Fact]
    public async Task SAP_is_asked_only_for_the_accounts_own_business_partner()
    {
        // The code has to come off the account. Nothing in the request body names a customer, and
        // nothing should be able to: this route is reached by a handset in a van.
        GivenVanRep();

        var asked = new List<string?>();
        var history = await WhenHistoryIsRead(RecordingSap(asked, Invoice(docEntry: 9004, docNum: 4024)));

        Assert.Single(history);
        Assert.Equal(VanBusinessPartner, Assert.Single(asked));
    }

    /// <summary>
    /// An account with no business partner reads no invoices, and asks SAP for none.
    /// </summary>
    /// <remarks>
    /// The failure this pins is not an empty screen, it is a full one: with the fiscal join gone, an
    /// unscoped read is every invoice the company raised in the window, on a handset that is shared
    /// and lives in a van.
    ///
    /// <para>What holds it up is the per-code loop — no codes, no requests — rather than the early
    /// return at the top of <c>GetInvoiceHistoryAsync</c>, which was found by mutation to be
    /// redundant and is kept for the log line and for stating the intent where it is read. So the
    /// assertion is on SAP never being called, not on the return value alone: the regression worth
    /// catching is a future rewrite that reads the window unfiltered and narrows afterwards, which
    /// is the shape this route had before, and an empty-list assertion would pass right through it.</para>
    /// </remarks>
    [Fact]
    public async Task An_account_with_no_business_partner_never_reaches_SAP()
    {
        GivenVanRep(businessPartner: null);

        var history = await WhenHistoryIsRead(
            StubProxy.For<ISAPServiceLayerClient>((method, _) => throw new InvalidOperationException(
                $"SAP must not be asked at all when the account has no scope, but {method.Name} was called.")));

        Assert.Empty(history);
    }

    [Fact]
    public async Task An_invoice_against_another_business_partner_is_not_listed()
    {
        // SAP is asked for one card code, so this should not arrive — but if a future change widens
        // the read, the filter behind it has to still hold. Belt and braces on the one boundary
        // that decides whether a rep sees another van's trading.
        GivenVanRep();

        var history = await WhenHistoryIsRead(SapHolds(
            Invoice(docEntry: 9005, docNum: 4025),
            Invoice(docEntry: 9006, docNum: 4026, cardCode: "C-VAN-099")));

        Assert.Equal("4025", Assert.Single(history).DocNum);
    }

    // ── What the document has to arrive carrying ────────────────────────

    /// <summary>
    /// The invoice's own lines come back with it, and the read asks SAP for them.
    /// </summary>
    /// <remarks>
    /// Nearly shipped without this. The read was moved onto <c>GetPagedInvoicesByOffsetAsync</c> to
    /// push the card code into SAP, and that method selected no <c>DocumentLines</c> at all — so
    /// every invoice would have arrived with an empty basket. Nothing would have failed: the list
    /// rows show a reference and a total, both of which survive, and only the detail page a rep opens
    /// in front of a customer would have been blank. The handset grosses an invoice off its lines
    /// too, falling back to a flat rate when there are none, so the totals would have quietly moved
    /// as well.
    ///
    /// <para>Both halves are asserted because they fail apart: the flag can be dropped while the
    /// mapping still works, and the mapping can break while the flag is still passed.</para>
    /// </remarks>
    [Fact]
    public async Task An_invoice_arrives_with_its_lines()
    {
        GivenVanRep();

        var askedForLines = new List<bool>();
        var invoice = Invoice(docEntry: 9007, docNum: 4027);
        invoice.DocumentLines =
        [
            new InvoiceLine { LineNum = 0, ItemCode = "MOZ-1KG", ItemDescription = "Mozzarella 1kg", Quantity = 3m, UnitPrice = 8m, LineTotal = 24m },
            new InvoiceLine { LineNum = 1, ItemCode = "FET-500", ItemDescription = "Feta 500g", Quantity = 2m, UnitPrice = 5m, LineTotal = 10m }
        ];

        var history = await WhenHistoryIsRead(RecordingSap(new List<string?>(), askedForLines, invoice));

        Assert.True(
            Assert.Single(askedForLines),
            "The invoice read must ask SAP to expand DocumentLines, or the detail page draws an empty document.");

        var listed = Assert.Single(history);
        Assert.Equal(2, listed.Item);
        Assert.Collection(
            listed.OrderItems,
            first =>
            {
                Assert.Equal("MOZ-1KG", first.Code);
                Assert.Equal("Mozzarella 1kg", first.Name);
                Assert.Equal(3, first.Quantity);
            },
            second =>
            {
                Assert.Equal("FET-500", second.Code);
                Assert.Equal(2, second.Quantity);
            });
    }

    // ── Given ───────────────────────────────────────────────────────────────

    private void GivenVanRep(string? businessPartner = VanBusinessPartner)
    {
        _context.Users.Add(new User
        {
            Id = Rep,
            Username = "van-rep",
            PasswordHash = "not-a-real-hash",
            Role = ApplicationRoles.Sales,
            IsActive = true,
            AssignedBusinessPartnerCode = businessPartner
        });

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private void GivenFiscalTransaction(
        int docNum,
        string? createdBy,
        string verification,
        DateTime? at = null)
    {
        _context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = $"test-{docNum}-{verification}",
            DocumentType = "Invoice",
            DocNum = docNum,
            Status = "Success",
            VerificationCode = verification,
            CreatedByUserId = createdBy,
            TimestampUtc = at ?? new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc)
        });

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    // ── When ────────────────────────────────────────────────────────────────

    private async Task<List<VanSalesLegacyOrderDto>> WhenHistoryIsRead(ISAPServiceLayerClient sap)
    {
        var handler = new GetVanSalesOrderHistoryHandler(
            _context,
            sap,
            NullLogger<GetVanSalesOrderHistoryHandler>.Instance);

        var result = await handler.Handle(
            new GetVanSalesOrderHistoryQuery(
                Rep,
                new VanSalesOrderSearchRequest
                {
                    // Invoices only. The sales order half reads local tables and is asserted
                    // elsewhere; mixing it in here would put rows in the list that say nothing
                    // about the scope under test.
                    Type = "INV",
                    StartDate = WindowStart,
                    EndDate = WindowEnd
                }),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? string.Join("; ", result.Errors) : string.Empty);
        return result.Value;
    }

    // ── SAP stand-ins ───────────────────────────────────────────────────────

    private static Invoice Invoice(
        int docEntry,
        int docNum,
        string cardCode = VanBusinessPartner) => new()
        {
            DocEntry = docEntry,
            DocNum = docNum,
            CardCode = cardCode,
            CardName = "Shop on the route",
            DocDate = "2026-08-20",
            DocDueDate = "2026-08-20",
            DocCurrency = "USD",
            DocTotal = 118m,
            VatSum = 18m,
            DocumentLines = []
        };

    /// <summary>A SAP that answers the card code it is asked for out of a fixed set.</summary>
    private static ISAPServiceLayerClient SapHolds(params Invoice[] invoices) =>
        RecordingSap(new List<string?>(), invoices);

    private static ISAPServiceLayerClient RecordingSap(List<string?> asked, params Invoice[] invoices) =>
        RecordingSap(asked, new List<bool>(), invoices);

    /// <summary>
    /// The same, recording every card code SAP was asked for and whether lines were wanted with it.
    /// </summary>
    /// <remarks>
    /// The card code filter is applied here rather than ignored, because that is what SAP does with
    /// the argument — a stub that returned everything regardless would let a handler that forgot to
    /// pass the code pass the test.
    /// </remarks>
    private static ISAPServiceLayerClient RecordingSap(
        List<string?> asked,
        List<bool> askedForLines,
        params Invoice[] invoices) =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) =>
        {
            if (method.Name != nameof(ISAPServiceLayerClient.GetPagedInvoicesByOffsetAsync))
            {
                throw new InvalidOperationException($"Unexpected SAP call: {method.Name}");
            }

            var cardCode = (string?)Argument(method, args, "cardCode");
            asked.Add(cardCode);
            askedForLines.Add((bool)(Argument(method, args, "includeDocumentLines") ?? false));

            return Task.FromResult(invoices
                .Where(invoice => string.Equals(invoice.CardCode, cardCode, StringComparison.OrdinalIgnoreCase))
                .ToList());
        });

    /// <summary>
    /// Reads one argument of the SAP call by parameter name.
    /// </summary>
    /// <remarks>
    /// By name rather than by position: <c>GetPagedInvoicesByOffsetAsync</c> is overloaded and
    /// carries eight optional parameters, so an argument index here would go quietly wrong the next
    /// time one is inserted — and going quietly wrong means asserting against whatever happens to
    /// sit at that index. Inserting <c>includeDocumentLines</c> ahead of the cancellation token is
    /// exactly that change, and it has already happened once.
    ///
    /// <para>A name that is not there throws rather than answering null, because the parameter going
    /// missing is itself the regression — a read that no longer takes a card code is a read that is
    /// no longer scoped.</para>
    /// </remarks>
    private static object? Argument(MethodInfo method, object?[]? args, string name)
    {
        var parameters = method.GetParameters();

        for (var index = 0; index < parameters.Length; index++)
        {
            if (parameters[index].Name == name)
            {
                return args?[index];
            }
        }

        throw new InvalidOperationException(
            $"{method.Name} has no {name} parameter, so what this test pins no longer exists.");
    }

    /// <summary>
    /// <see cref="SalesOrderEntity.RowVersion"/> is <c>[Timestamp]</c>, which Npgsql maps to the
    /// store-generated <c>xmin</c> system column. SQLite has no equivalent, so EF leaves the column
    /// out of the INSERT and the NOT NULL constraint fails.
    /// </summary>
    private sealed class SqliteApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<SalesOrderEntity>()
                .Property(order => order.RowVersion)
                .ValueGeneratedNever()
                .IsConcurrencyToken(false);
        }
    }
}
