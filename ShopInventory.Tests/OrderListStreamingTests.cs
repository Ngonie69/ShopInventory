using System.Net;
using System.Text;
using Blazored.LocalStorage;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that the Web's two big order-list reads stream the API body instead of holding it whole.
/// </summary>
/// <remarks>
/// Mobile Orders and Purchase Orders ask for up to 10,000 orders with their lines. Each read used to
/// buffer the body into a byte array, copy it into a UTF-16 string on the large object heap, and only
/// then deserialize, which left tens of MB per page load for the GC to find. These tests answer with
/// content that refuses to be buffered, so a return to <c>GetAsync(url)</c> without
/// <c>ResponseHeadersRead</c>, or to <c>ReadAsStringAsync</c>, fails here.
/// </remarks>
public sealed class OrderListStreamingTests
{
    private const string SalesOrdersBody = """
        {"page":1,"pageSize":10000,"totalCount":2,"totalPages":1,"orders":[
          {"id":11,"sapDocEntry":901,"sapDocNum":5001,"orderNumber":"SO-11","orderDate":"2026-10-06T08:15:00Z","cardCode":"C001","cardName":"Alpha Stores","status":1,
           "lines":[{"id":1,"lineNum":0,"itemCode":"MILK1L","itemDescription":"Milk 1L","quantity":12},{"id":2,"lineNum":1,"itemCode":"YOG500","quantity":6}]},
          {"id":12,"orderNumber":"SO-12","orderDate":"2026-10-06T09:00:00Z","cardCode":"C002","status":0,"lines":[]}
        ]}
        """;

    private const string PurchaseOrdersBody = """
        {"page":1,"pageSize":10000,"totalCount":1,"totalPages":1,"orders":[
          {"id":7,"sapDocEntry":44,"orderNumber":"PO-7","orderDate":"2026-10-01T00:00:00Z","cardCode":"S001","lines":[{"lineNum":0,"itemCode":"CREAM5L","quantity":3}]}
        ]}
        """;

    [Fact]
    public async Task Sales_orders_are_read_from_the_stream_with_their_lines()
    {
        var handler = new StreamOnlyHandler(HttpStatusCode.OK, SalesOrdersBody);
        var service = CreateSalesOrderService(handler);

        var result = await service.GetSalesOrdersAsync(pageSize: 10000);

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(["SO-11", "SO-12"], result.Orders.Select(o => o.OrderNumber));
        var first = result.Orders[0];
        Assert.Equal(901, first.SAPDocEntry);
        Assert.Equal(["MILK1L", "YOG500"], first.Lines.Select(l => l.ItemCode));
        Assert.Equal(6, first.Lines[1].Quantity);
        Assert.Empty(result.Orders[1].Lines);
    }

    [Fact]
    public async Task Purchase_orders_are_read_from_the_stream_with_their_lines()
    {
        var handler = new StreamOnlyHandler(HttpStatusCode.OK, PurchaseOrdersBody);
        var service = new PurchaseOrderService(Client(handler), NullLogger<PurchaseOrderService>.Instance);

        var local = await service.GetPurchaseOrdersAsync(pageSize: 10000);
        var fromSap = await service.GetPurchaseOrdersFromSAPAsync(pageSize: 10000);

        foreach (var result in new[] { local, fromSap })
        {
            Assert.NotNull(result);
            var order = Assert.Single(result.Orders);
            Assert.Equal("PO-7", order.OrderNumber);
            Assert.Equal("CREAM5L", Assert.Single(order.Lines).ItemCode);
        }
    }

    [Fact]
    public async Task An_error_status_still_returns_null()
    {
        var handler = new StreamOnlyHandler(HttpStatusCode.InternalServerError, "{\"message\":\"boom\"}");

        Assert.Null(await CreateSalesOrderService(handler).GetSalesOrdersAsync());
        Assert.Null(await new PurchaseOrderService(Client(handler), NullLogger<PurchaseOrderService>.Instance).GetPurchaseOrdersAsync());
    }

    private static SalesOrderService CreateSalesOrderService(HttpMessageHandler handler)
    {
        var httpClient = Client(handler);
        var auditContext = new WebClientAuditContext();
        var authStateProvider = new CustomAuthStateProvider(
            StubProxy.Unused<ILocalStorageService>(),
            httpClient,
            NullLogger<CustomAuthStateProvider>.Instance,
            auditContext);

        return new SalesOrderService(
            httpClient,
            NullLogger<SalesOrderService>.Instance,
            StubProxy.Unused<ILocalStorageService>(),
            authStateProvider,
            auditContext);
    }

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:5106/") };

    private sealed class StreamOnlyHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                // An error body is small and is read whole for the log, as before.
                Content = (int)status < 400
                    ? new StreamOnlyContent(Encoding.UTF8.GetBytes(body))
                    : new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
    }

    /// <summary>
    /// Serves its body only as a stream. HttpClient buffers a response (the default
    /// ResponseContentRead) through SerializeToStreamAsync, so that path fails the test.
    /// </summary>
    private sealed class StreamOnlyContent : HttpContent
    {
        private readonly byte[] _body;

        public StreamOnlyContent(byte[] body)
        {
            _body = body;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(_body, writable: false));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("The response body was buffered instead of streamed.");

        protected override bool TryComputeLength(out long length)
        {
            length = _body.Length;
            return true;
        }
    }
}
