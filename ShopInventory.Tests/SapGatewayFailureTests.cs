using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What an invoice post reports when the balancer in front of the Service Layer answers instead of SAP,
/// and what the availability probe's ping reports.
/// </summary>
/// <remarks>
/// A gateway page used to be read as SAP refusing the invoice. That told the posting passes two wrong
/// things at once: that the failure was permanent, so an outage spent every sale's attempts; and that
/// nothing was created, so a sale that SAP may have committed behind the gateway was sent again.
/// </remarks>
[Collection("SapServiceLayerClient")]
public sealed class SapGatewayFailureTests
{
    private const string GatewayPage =
        "<html><head><title>502 Proxy Error</title></head><body>The proxy server received an invalid response.</body></html>";

    private const string SapRefusal =
        "{\"error\":{\"code\":-10,\"message\":{\"lang\":\"en-us\",\"value\":\"Enter a valid customer code\"}}}";

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_gateway_page_leaves_the_invoice_unknown_and_the_failure_transient(HttpStatusCode status)
    {
        var sap = new ScriptedServiceLayer { PostStatus = status, PostBody = GatewayPage, PostMediaType = "text/html" };

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(sap).CreateInvoiceAsync(Invoice()));

        Assert.Equal(status, failure.StatusCode);
        Assert.True(SapFailureClassifier.IsTransient(failure));
        Assert.False(SapFailureClassifier.DefinitelyNotCommitted(failure));
    }

    [Fact]
    public async Task A_refusal_in_sap_s_own_words_is_still_a_refusal()
    {
        var sap = new ScriptedServiceLayer { PostStatus = HttpStatusCode.BadRequest, PostBody = SapRefusal };

        var failure = await Assert.ThrowsAsync<SapRequestRejectedException>(() => CreateClient(sap).CreateInvoiceAsync(Invoice()));

        Assert.Contains("valid customer code", failure.SapMessage);
        Assert.False(SapFailureClassifier.IsTransient(failure));
        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(failure));
    }

    [Theory]
    [InlineData(Document.Invoice)]
    [InlineData(Document.CreditNote)]
    [InlineData(Document.IncomingPayment)]
    public async Task A_server_error_sap_itself_explains_is_a_refusal_too(Document document)
    {
        // The envelope is the evidence SAP answered, whatever the status. A business refusal SAP
        // happens to send as a 500 is still no, and nothing was created.
        var sap = new ScriptedServiceLayer { PostStatus = HttpStatusCode.InternalServerError, PostBody = SapRefusal };

        var failure = await Assert.ThrowsAsync<SapRequestRejectedException>(() => PostAsync(CreateClient(sap), document));

        Assert.False(SapFailureClassifier.IsTransient(failure));
        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(failure));
    }

    [Theory]
    [InlineData(Document.Invoice)]
    [InlineData(Document.CreditNote)]
    [InlineData(Document.IncomingPayment)]
    public async Task A_post_that_meets_sap_s_database_down_is_transient_and_leaves_the_document_unknown(Document document)
    {
        // The same outage as the duplicate check below, met by the post itself. It used to be read as
        // SAP refusing the document: permanent, so every pass spent one of the sale's six attempts,
        // and not committed, so the sale was free to be sent again without a lookup.
        var sap = new ScriptedServiceLayer { PostStatus = HttpStatusCode.InternalServerError, PostBody = DatabaseDown };

        var failure = await Assert.ThrowsAsync<SapBackendUnreachableException>(() => PostAsync(CreateClient(sap), document));

        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.Contains("Couldn't connect to server", failure.Message);
        Assert.True(SapFailureClassifier.IsTransient(failure));

        // Transient is not "not committed": a write's 5xx leaves the document's existence to a lookup.
        Assert.False(SapFailureClassifier.DefinitelyNotCommitted(failure));
    }

    [Fact]
    public async Task A_login_that_meets_sap_s_database_down_is_transient_and_sends_nothing()
    {
        // A posting pass whose session lapsed logs in again first. That 500 used to be a bare
        // "SAP Login failed: InternalServerError", which no availability word matched.
        var sap = new ScriptedServiceLayer { LoginStatus = HttpStatusCode.InternalServerError, LoginBody = DatabaseDown };

        var failure = await Assert.ThrowsAsync<SapReadFailedException>(
            async () => await (await LoggedOutClientAsync(sap)).CreateInvoiceAsync(Invoice()));

        Assert.Equal("SAP Login failed: InternalServerError", failure.Message);
        Assert.True(SapFailureClassifier.IsTransient(failure));
        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(failure));
    }

    [Fact]
    public async Task A_login_sap_refuses_is_still_permanent()
    {
        var sap = new ScriptedServiceLayer { LoginStatus = HttpStatusCode.Unauthorized, LoginBody = SapRefusal };

        var failure = await Assert.ThrowsAsync<SapReadFailedException>(
            async () => await (await LoggedOutClientAsync(sap)).CreateInvoiceAsync(Invoice()));

        Assert.False(SapFailureClassifier.IsTransient(failure));
    }

    [Fact]
    public async Task A_refusal_that_mentions_connecting_is_still_a_refusal_below_500()
    {
        // The words alone never decide it: a 4xx is SAP judging the request, whatever it says.
        var body = "{\"error\":{\"code\":-10,\"message\":{\"lang\":\"en-us\",\"value\":\"Failed to connect the payment to invoice 12\"}}}";
        var sap = new ScriptedServiceLayer { PostStatus = HttpStatusCode.BadRequest, PostBody = body };

        var failure = await Assert.ThrowsAsync<SapRequestRejectedException>(() => CreateClient(sap).CreateInvoiceAsync(Invoice()));

        Assert.True(SapFailureClassifier.DefinitelyNotCommitted(failure));
    }

    /// <summary>
    /// SAP's database down behind a Service Layer that still answers — production, 14 Sep 2026.
    /// </summary>
    /// <remarks>
    /// Till sale KEF-FAC-20260914-8DFB97306920 met this body on its duplicate check and every minute's
    /// pass spent an attempt on it: no word of it matched an availability signal ("connect" is not
    /// "connection", "InternalServerError" is not "500"), so six attempts went in six minutes and the
    /// sale was left fiscalised and unposted for a month, its 40 cones held off the shop's shelf.
    /// </remarks>
    private const string DatabaseDown =
        "{\n   \"error\" : {\n      \"code\" : 7,\n      \"message\" : {\n         \"lang\" : \"en-us\",\n"
        + "         \"value\" : \"Failed to connect to kfldb port 40020 after 0 ms: Couldn't connect to server\"\n"
        + "      }\n   }\n}\n";

    [Fact]
    public async Task A_duplicate_check_sap_could_not_answer_for_its_database_is_transient()
    {
        var sap = new ScriptedServiceLayer { LookupStatus = HttpStatusCode.InternalServerError, LookupBody = DatabaseDown };

        var read = await Assert.ThrowsAnyAsync<Exception>(
            () => CreateClient(sap).GetInvoiceByVanSaleOrderAsync("KEF-FAC-20260914-8DFB97306920"));

        // The wording the operator and the exception centre read is unchanged.
        Assert.Equal($"Failed to check invoice by U_Van_saleorder: InternalServerError - {DatabaseDown}", read.Message);

        // And the status travels with it, so the answer does not rest on the wording at all.
        Assert.Equal(HttpStatusCode.InternalServerError, Assert.IsType<SapReadFailedException>(read).StatusCode);

        // Wrapped exactly as DesktopSalePostingService.FindAlreadyPostedAsync wraps it.
        var posting = new InvalidOperationException(
            "Not posting till sale KEF-FAC-20260914-8DFB97306920: SAP could not be asked whether it "
            + $"already holds this invoice. {read.Message}", read);

        Assert.True(SapFailureClassifier.IsTransient(read));
        Assert.True(SapFailureClassifier.IsTransient(posting));
    }

    [Fact]
    public async Task A_read_sap_refuses_as_malformed_is_still_permanent()
    {
        // A 400 is a query SAP will never accept, however often it is asked.
        var sap = new ScriptedServiceLayer { LookupStatus = HttpStatusCode.BadRequest, LookupBody = SapRefusal };

        var read = await Assert.ThrowsAnyAsync<Exception>(
            () => CreateClient(sap).GetInvoiceByVanSaleOrderAsync("KEF-FAC-20260914-8DFB97306920"));

        Assert.Equal(HttpStatusCode.BadRequest, Assert.IsType<SapReadFailedException>(read).StatusCode);
        Assert.False(SapFailureClassifier.IsTransient(new InvalidOperationException("Not posting", read)));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public void A_failed_read_is_judged_by_its_status_not_its_words(HttpStatusCode status, bool transient)
    {
        // Wording chosen to say nothing either way, so only the status can decide.
        var read = new SapReadFailedException(status, $"Failed to get items: {status} - {{}}");

        Assert.Equal(transient, SapFailureClassifier.IsTransient(read));
        Assert.Equal(transient, SapFailureClassifier.IsTransient(new InvalidOperationException("Not posting till sale X.", read)));
    }

    [Fact]
    public async Task The_ping_passes_when_sap_serves_the_request()
    {
        var sap = new ScriptedServiceLayer();

        await CreateClient(sap).PingAsync();

        Assert.Equal(1, sap.Pings);
    }

    [Fact]
    public async Task The_ping_throws_when_sap_does_not_serve_it()
    {
        var sap = new ScriptedServiceLayer { PingStatus = HttpStatusCode.ServiceUnavailable };

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(sap).PingAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
    }

    [Fact]
    public async Task The_ping_asks_again_when_the_session_has_lapsed()
    {
        // A lapsed session is not SAP being down: the probe must not count it as a failure.
        var sap = new ScriptedServiceLayer { UnauthorizedPings = 1 };

        await CreateClient(sap).PingAsync();

        Assert.Equal(2, sap.Pings);
    }

    public enum Document
    {
        Invoice,
        CreditNote,
        IncomingPayment
    }

    private static Task PostAsync(SAPServiceLayerClient client, Document document) => document switch
    {
        Document.Invoice => client.CreateInvoiceAsync(Invoice()),
        Document.CreditNote => client.CreateCreditNoteAsync(new CreateCreditNoteRequest
        {
            CardCode = "COR007",
            Lines = [new() { ItemCode = "MUE009", Quantity = 1, UnitPrice = 0.37m, WarehouseCode = "KEFGRS" }]
        }),
        Document.IncomingPayment => client.CreateIncomingPaymentAsync(new CreateIncomingPaymentRequest
        {
            CardCode = "COR007",
            CashSum = 0.37m
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(document))
    };

    private static CreateInvoiceRequest Invoice() => new()
    {
        CardCode = "COR007",
        DocCurrency = "USD",
        Lines = [new() { ItemCode = "MUE009", Quantity = 1, UnitPrice = 0.37m, WarehouseCode = "KEFGRS" }]
    };

    /// <summary>The session is shared by every client in the process, so a login has to be forced.</summary>
    private static async Task<SAPServiceLayerClient> LoggedOutClientAsync(ScriptedServiceLayer sap)
    {
        var client = CreateClient(sap);
        await client.LogoutAsync();
        return client;
    }

    private static SAPServiceLayerClient CreateClient(ScriptedServiceLayer sap)
    {
        var httpClient = new HttpClient(sap) { BaseAddress = new Uri("https://sap.invalid/b1s/v1/") };
        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(new SAPSettings { ServiceLayerUrl = "https://sap.invalid/b1s/v1/" }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            StubProxy.Unused<ISapItemUomMappingStore>());
    }

    private sealed class ScriptedServiceLayer : HttpMessageHandler
    {
        public HttpStatusCode PostStatus { get; init; } = HttpStatusCode.Created;
        public string PostBody { get; init; } = "{\"DocEntry\":501,\"DocNum\":601,\"DocumentLines\":[]}";
        public string PostMediaType { get; init; } = "application/json";
        public HttpStatusCode PingStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode LookupStatus { get; init; } = HttpStatusCode.OK;
        public string LookupBody { get; init; } = "{\"value\":[]}";
        public HttpStatusCode LoginStatus { get; init; } = HttpStatusCode.OK;
        public string? LoginBody { get; init; }
        public int UnauthorizedPings { get; set; }
        public int Pings { get; private set; }
        public int Logins { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var target = request.RequestUri!.PathAndQuery;

            if (target.EndsWith("/Logout", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                Logins++;
                return Task.FromResult(Respond(LoginStatus, LoginBody ?? $"{{\"SessionId\":\"session-{Logins}\"}}"));
            }

            if ((target.EndsWith("/Invoices", StringComparison.Ordinal)
                 || target.EndsWith("/CreditNotes", StringComparison.Ordinal)
                 || target.EndsWith("/IncomingPayments", StringComparison.Ordinal))
                && request.Method == HttpMethod.Post)
            {
                return Task.FromResult(Respond(PostStatus, PostBody, PostMediaType));
            }

            if (target.Contains("/Invoices?$filter=U_Van_saleorder", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Respond(LookupStatus, LookupBody));
            }

            if (target.Contains("/Warehouses?$select=WarehouseCode&$top=1", StringComparison.Ordinal))
            {
                Pings++;
                if (UnauthorizedPings > 0)
                {
                    UnauthorizedPings--;
                    return Task.FromResult(Respond(HttpStatusCode.Unauthorized, "{}"));
                }

                return Task.FromResult(Respond(PingStatus, "{\"value\":[{\"WarehouseCode\":\"KEFGRS\"}]}"));
            }

            throw new InvalidOperationException($"Unexpected SAP request: {request.Method} {target}");
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, string body, string mediaType = "application/json") =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
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
