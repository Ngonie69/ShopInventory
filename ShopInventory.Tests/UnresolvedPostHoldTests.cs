using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Web.Services;
using CreateCreditNoteRequest = ShopInventory.DTOs.CreateCreditNoteRequest;
using CreateIncomingPaymentRequest = ShopInventory.Services.CreateIncomingPaymentRequest;
using CreateInvoiceRequest = ShopInventory.Models.CreateInvoiceRequest;

namespace ShopInventory.Tests;

/// <summary>
/// What a sale looks like while it is held after a post whose outcome is unknown, and which failures
/// hold it at all.
/// </summary>
/// <remarks>
/// <para><b>The report this is here for.</b> INV336 (KEF-FAC-20260916-A8CE3BC7D6BE, 16 September
/// 2026) showed "SAP has not accepted this sale" with a notice that a post had gone out at 13:34Z.
/// SAP had no invoice for it — correctly, since the post had not landed — and at 15:50 CAT it posted
/// on its own as invoice 775370. Nothing was wrong with the sale. Three things about how the hold was
/// presented were:</para>
/// <list type="number">
/// <item>the notice replaced the sale's recorded error on every pass in the window, so what the post
/// actually failed with could not be read anywhere;</item>
/// <item>nothing said when the sale would be sent again, and the page called the wait a refusal;</item>
/// <item>a failure before the invoice left — logging in, the series lookup, an open circuit — held a
/// sale for the whole window over a post SAP never received.</item>
/// </list>
/// </remarks>
[Collection("SapServiceLayerClient")]
public sealed class UnresolvedPostHoldTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly ScriptedSapClient _sap = new();

    public UnresolvedPostHoldTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---------------------------------------------------------------
    // 1. The recorded error survives the hold
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_held_sale_keeps_what_its_post_failed_with()
    {
        var sale = await GivenSaleAsync();
        _sap.CreateFails = () => new TimeoutException("The SAP Service Layer did not respond in time.");

        await Service().PostPendingSalesAsync();

        var afterPost = await ReloadAsync(sale.Id);
        Assert.NotNull(afterPost.PostIssuedAtUtc);
        Assert.Equal("The SAP Service Layer did not respond in time.", afterPost.LastPostingError);

        // The next pass, a minute later, falls inside the window and leaves the sale out altogether:
        // it used to load it, ask SAP, and write its own notice over the timeout. The timeout is the
        // only thing that says why the sale is not in SAP, and nothing is sent.
        _sap.CreateFails = null;
        var held = await Service().PostPendingSalesAsync();

        Assert.Equal(0, held.Total);
        Assert.Single(_sap.CreateCalls);

        var duringHold = await ReloadAsync(sale.Id);
        Assert.Equal("The SAP Service Layer did not respond in time.", duringHold.LastPostingError);
        Assert.Equal(0, duringHold.PostingAttempts);
    }

    [Fact]
    public async Task A_held_sale_with_no_recorded_reply_says_so()
    {
        // A post whose reply never came back at all — a restart between the marker and the answer —
        // leaves nothing recorded. The sale still has to show why it is not in SAP.
        var sale = await GivenSaleAsync(postIssuedAtUtc: DateTime.UtcNow.AddMinutes(-2));

        await Service().PostPendingSalesAsync();

        var held = await ReloadAsync(sale.Id);
        Assert.Empty(_sap.CreateCalls);
        Assert.Equal(
            $"No reply to the post was recorded. Check SAP for U_Van_saleorder '{sale.ExternalReferenceId}'.",
            held.LastPostingError);
    }

    // ---------------------------------------------------------------
    // 2. The hold says when it ends
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_post_pressed_during_the_hold_says_when_the_sale_goes_again()
    {
        var issuedAt = new DateTime(2026, 9, 16, 13, 34, 24, DateTimeKind.Utc);

        // Pinned to the moment of the report rather than to now, so the times read as they did.
        var message = UnresolvedPostHold.Describe(issuedAt, graceMinutes: 15);

        Assert.Equal(
            "A post sent to SAP at 16 Sep 2026 15:34:24 CAT got no clear answer, so SAP may already hold the "
            + "invoice. It is not sent again until 16 Sep 2026 15:49:24 CAT, when SAP is asked again first.",
            message);

        // And that is what a press of Post to SAP inside the window answers with.
        var recent = await GivenSaleAsync(
            reference: "KEF-FAC-20260916-RECENT",
            postIssuedAtUtc: DateTime.UtcNow.AddMinutes(-1),
            lastPostingError: "The SAP Service Layer did not respond in time.");

        var result = await Service().PostSaleAsync(recent.Id);

        var error = Assert.Single(result!.Errors);
        var stored = (await ReloadAsync(recent.Id)).PostIssuedAtUtc!.Value;
        Assert.Equal($"{recent.ExternalReferenceId}: {UnresolvedPostHold.Describe(stored, 15)}", error);
        Assert.Empty(_sap.CreateCalls);
    }

    [Fact]
    public async Task The_sales_list_carries_when_a_held_sale_goes_again_through_to_the_web_model()
    {
        var issuedAt = DateTime.UtcNow.AddMinutes(-4);
        await GivenSaleAsync(reference: "HELD", postIssuedAtUtc: issuedAt, lastPostingError: "timed out");
        await GivenSaleAsync(reference: "EXPIRED", postIssuedAtUtc: DateTime.UtcNow.AddMinutes(-40), lastPostingError: "timed out");
        await GivenSaleAsync(reference: "REFUSED", lastPostingError: "SAP refused to create the invoice: item is blocked");
        await GivenSaleAsync(reference: "VAN-HELD", source: SaleSourceSystems.VanSales, postIssuedAtUtc: issuedAt);

        var sales = (await ListThroughTheWebAsync(tillGraceMinutes: 15, vanGraceMinutes: 30))
            .Sales.ToDictionary(s => s.ExternalReferenceId);

        // Round-tripped through JSON, so compare to the millisecond the wire keeps.
        AssertSameInstant(issuedAt.AddMinutes(15), sales["HELD"].PostHeldUntilUtc);
        Assert.Null(sales["EXPIRED"].PostHeldUntilUtc);
        Assert.Null(sales["REFUSED"].PostHeldUntilUtc);

        // The van route waits out its own window, not the till's.
        AssertSameInstant(issuedAt.AddMinutes(30), sales["VAN-HELD"].PostHeldUntilUtc);
    }

    [Fact]
    public async Task A_posted_sale_is_never_described_as_held()
    {
        // The marker is never cleared on success; the invoice number is what says it is done.
        await GivenSaleAsync(reference: "POSTED", postIssuedAtUtc: DateTime.UtcNow.AddMinutes(-1), sapDocEntry: 775370);

        var sale = Assert.Single((await ListThroughTheWebAsync(15, 15)).Sales);

        Assert.Null(sale.PostHeldUntilUtc);
    }

    // ---------------------------------------------------------------
    // 3. A failure before the invoice leaves does not hold the sale
    // ---------------------------------------------------------------

    [Fact]
    public void An_open_circuit_proves_nothing_was_sent()
    {
        // The handler throws it before handing the request to the network.
        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(
            new SapCircuitOpenException("SAP circuit breaker is open. Retry after 30 seconds.")));
    }

    [Fact]
    public void A_marked_failure_proves_nothing_was_sent_and_keeps_its_type_and_transience()
    {
        var loginFailure = new HttpRequestException("No connection could be made to the Service Layer.");
        var postFailure = new HttpRequestException("No connection could be made to the Service Layer.");

        Assert.False(SapFailureClassifier.MarkNotSent(loginFailure));

        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(loginFailure));
        // The negative control: the same exception, unmarked, is a post that may have landed.
        Assert.False(SapFailureClassifier.DefinitelyNotCommitted(postFailure));

        // Still an outage as far as the attempt budget is concerned.
        Assert.True(SapFailureClassifier.IsTransient(loginFailure));
    }

    /// <summary>
    /// Every document a posting service writes a marker for before sending: the till and van
    /// invoice, the desktop credit memo, and the daily incoming payment.
    /// </summary>
    public static TheoryData<string> Documents => new() { "invoice", "credit memo", "incoming payment" };

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task The_client_marks_a_failed_login_as_never_sent(string document)
    {
        var sap = new FakeServiceLayer { FailLogin = true };

        var client = await CreateClientAsync(sap);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(client, document));

        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(failure));
        Assert.Equal(0, sap.DocumentPosts);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task The_client_marks_a_failed_login_after_a_401_as_never_sent(string document)
    {
        // SAP answered the first post with a 401, so it created nothing, and the login that fails
        // comes before the second post is sent.
        var sap = new FakeServiceLayer { UnauthorizedFirstPost = true, FailLoginAfterFirst = true };

        var client = await CreateClientAsync(sap);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(client, document));

        Assert.Equal(1, sap.DocumentPosts);
        Assert.Equal(2, sap.Logins);
        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(failure));
    }

    [Fact]
    public async Task The_client_marks_a_failed_series_lookup_as_never_sent()
    {
        var sap = new FakeServiceLayer { FailEverythingElse = true };

        var client = await CreateClientAsync(sap, new SAPSettings
        {
            ServiceLayerUrl = "https://sap.invalid/b1s/v1/",
            InvoiceSeriesName = "SHOP"
        });

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => client.CreateInvoiceAsync(InvoiceRequest()));

        Assert.True(sap.OtherRequests > 0, "the series lookup should have reached SAP");
        Assert.Equal(0, sap.DocumentPosts);
        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(failure));
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task The_client_does_not_mark_a_post_that_went_out(string document)
    {
        // The case the hold exists for, and it must stay held: the request left, and SAP may have it.
        var sap = new FakeServiceLayer { FailDocumentPost = true };

        var client = await CreateClientAsync(sap);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(client, document));

        Assert.Equal(1, sap.DocumentPosts);
        Assert.False(SapFailureClassifier.DefinitelyNotCommitted(failure));
    }

    [Fact]
    public async Task A_sale_whose_login_failed_is_not_held_and_posts_on_the_next_pass()
    {
        var sale = await GivenSaleAsync();
        _sap.CreateFails = () =>
        {
            var loginFailure = new HttpRequestException("No connection could be made to the Service Layer.");
            SapFailureClassifier.MarkNotSent(loginFailure);
            return loginFailure;
        };

        await Service().PostPendingSalesAsync();

        var afterFailure = await ReloadAsync(sale.Id);
        Assert.Null(afterFailure.PostIssuedAtUtc);
        Assert.Equal(0, afterFailure.PostingAttempts);

        _sap.CreateFails = null;
        var retried = await Service().PostPendingSalesAsync();

        Assert.Equal(1, retried.Posted);
        Assert.NotNull((await ReloadAsync(sale.Id)).SapDocNum);
    }

    // ---------------------------------------------------------------

    private static void AssertSameInstant(DateTime expected, DateTime? actual)
    {
        Assert.NotNull(actual);
        Assert.True(
            Math.Abs((expected - actual!.Value.ToUniversalTime()).TotalMilliseconds) < 1,
            $"expected {expected:O}, got {actual:O}");
    }

    private async Task<DesktopSaleEntity> GivenSaleAsync(
        string reference = "KEF-FAC-20260916-A8CE3BC7D6BE",
        string source = SaleSourceSystems.ShopTill,
        DateTime? postIssuedAtUtc = null,
        string? lastPostingError = null,
        int? sapDocEntry = null)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = source,
            CardCode = "COR011",
            // Today, so the automatic pass's lookback window includes it.
            DocDate = DateTime.UtcNow.Date,
            TotalAmount = 12.66m,
            VatAmount = 1.70m,
            Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            ConsolidationStatus = sapDocEntry.HasValue
                ? DesktopSaleConsolidationStatus.Consolidated
                : DesktopSaleConsolidationStatus.Pending,
            SapDocEntry = sapDocEntry,
            SapDocNum = sapDocEntry,
            WarehouseCode = "KEFBYS",
            PaymentMethod = TenderTypes.Cash,
            PostIssuedAtUtc = postIssuedAtUtc,
            LastPostingError = lastPostingError,
            CreatedBy = "cashier",
            CreatedAt = DateTime.UtcNow,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "ICA004",
                    Quantity = 1m,
                    UnitPrice = 10m,
                    LineTotal = 10m,
                    WarehouseCode = "KEFBYS"
                }
            ]
        };

        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();
        return sale;
    }

    private async Task<DesktopSaleEntity> ReloadAsync(int id) =>
        await _context.DesktopSales.AsNoTracking().SingleAsync(s => s.Id == id);

    private DesktopSalePostingService Service() =>
        new(
            _context,
            _sap.Client,
            new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            SaleBatchAllocators.Holding(),
            SalePostGuards.Backed(_connection),
            DesktopCreditPosters.Idle(_context),
            Options.Create(new DesktopSalePostingSettings()),
            NullLogger<DesktopSalePostingService>.Instance);

    private async Task<DesktopSalesListResponse> ListThroughTheWebAsync(int tillGraceMinutes, int vanGraceMinutes)
    {
        var caller = Guid.NewGuid();
        _context.Users.Add(new User
        {
            Id = caller,
            Username = $"console{caller:N}"[..12],
            PasswordHash = "x",
            Role = ApplicationRoles.Cashier,
            IsActive = true,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await new GetDesktopSalesHandler(
                _context,
                new RecordingAuditService(),
                Options.Create(new FiscalisationSettings()),
                Options.Create(new DesktopSalePostingSettings { UnresolvedPostGraceMinutes = tillGraceMinutes }),
                Options.Create(new VanSalesPostingSettings { UnresolvedPostGraceMinutes = vanGraceMinutes }))
            .Handle(new GetDesktopSalesQuery(caller), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);

        // What the Web's HttpClient does to the API's answer: web JSON out, web JSON in.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return JsonSerializer.Deserialize<DesktopSalesListResponse>(
            JsonSerializer.Serialize(result.Value, options), options)!;
    }

    private static Task SendAsync(SAPServiceLayerClient client, string document) => document switch
    {
        "invoice" => client.CreateInvoiceAsync(InvoiceRequest()),
        "credit memo" => client.CreateCreditNoteAsync(new CreateCreditNoteRequest
        {
            CardCode = "COR011",
            SapReference = "DCN-000123",
            OriginalInvoiceDocEntry = 2372617,
            Lines = [new() { ItemCode = "ICA004", Quantity = 1, UnitPrice = 10m, OriginalInvoiceLineId = 0 }]
        }),
        "incoming payment" => client.CreateIncomingPaymentAsync(new CreateIncomingPaymentRequest
        {
            CardCode = "COR011",
            DocDate = "2026-09-16",
            CashSum = 12.66m,
            PaymentInvoices = [new() { DocEntry = 2372617, SumApplied = 12.66m }]
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, null)
    };

    private static CreateInvoiceRequest InvoiceRequest() => new()
    {
        CardCode = "COR011",
        DocCurrency = "USD",
        U_Van_saleorder = "KEF-FAC-20260916-A8CE3BC7D6BE",
        Lines = [new() { ItemCode = "ICA004", Quantity = 1, UnitPrice = 10m, WarehouseCode = "KEFBYS" }]
    };

    /// <remarks>
    /// The client's SAP session is static, shared by every instance, so a session another test left
    /// behind would skip the login this class needs to fail. Logging out clears it.
    /// </remarks>
    private static async Task<SAPServiceLayerClient> CreateClientAsync(FakeServiceLayer sap, SAPSettings? settings = null)
    {
        var client = CreateClient(sap, settings);
        await client.LogoutAsync();
        return client;
    }

    private static SAPServiceLayerClient CreateClient(FakeServiceLayer sap, SAPSettings? settings)
    {
        var httpClient = new HttpClient(sap) { BaseAddress = new Uri("https://sap.invalid/b1s/v1/") };
        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(settings ?? new SAPSettings { ServiceLayerUrl = "https://sap.invalid/b1s/v1/" }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            StubProxy.Unused<ISapItemUomMappingStore>());
    }

    /// <summary>The posting service's view of SAP: nothing held already, and a post that can be made to fail.</summary>
    private sealed class ScriptedSapClient
    {
        public List<CreateInvoiceRequest> CreateCalls { get; } = [];
        public Func<Exception>? CreateFails { get; set; }

        private int _nextDocNum = 775370;

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) => (object)Task.FromResult<Invoice?>(null),
            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => Create((CreateInvoiceRequest)args![0]!),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        private Task<Invoice> Create(CreateInvoiceRequest request)
        {
            CreateCalls.Add(request);
            if (CreateFails is { } fail)
            {
                return Task.FromException<Invoice>(fail());
            }

            var docNum = _nextDocNum++;
            return Task.FromResult(new Invoice { DocEntry = docNum, DocNum = docNum });
        }
    }

    private sealed class FakeServiceLayer : HttpMessageHandler
    {
        public bool FailLogin { get; init; }
        public bool FailLoginAfterFirst { get; init; }
        public bool UnauthorizedFirstPost { get; init; }
        public bool FailDocumentPost { get; init; }
        public bool FailEverythingElse { get; init; }
        public int Logins { get; private set; }
        public int DocumentPosts { get; private set; }
        public int OtherRequests { get; private set; }

        private static readonly string[] DocumentSets = ["/Invoices", "/CreditNotes", "/IncomingPayments"];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.AbsolutePath;

            if (target.EndsWith("/Logout", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                Logins++;
                if (FailLogin || (FailLoginAfterFirst && Logins > 1))
                {
                    throw new HttpRequestException("No connection could be made to the Service Layer.");
                }

                return Task.FromResult(Json("{\"SessionId\":\"test-session\",\"SessionTimeout\":30}"));
            }

            if (request.Method == HttpMethod.Post
                && DocumentSets.Any(set => target.EndsWith(set, StringComparison.Ordinal)))
            {
                DocumentPosts++;
                if (UnauthorizedFirstPost && DocumentPosts == 1)
                {
                    return Task.FromResult(Json("{\"error\":{\"code\":301,\"message\":\"Invalid session.\"}}", HttpStatusCode.Unauthorized));
                }

                if (FailDocumentPost)
                {
                    throw new HttpRequestException("The connection was closed unexpectedly.");
                }

                return Task.FromResult(Json("{\"DocEntry\":1,\"DocNum\":1,\"CardCode\":\"COR011\"}", HttpStatusCode.Created));
            }

            OtherRequests++;
            if (FailEverythingElse)
            {
                throw new HttpRequestException("No connection could be made to the Service Layer.");
            }

            throw new InvalidOperationException($"Unexpected SAP request: {request.Method} {target}");
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ShopInventory.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
