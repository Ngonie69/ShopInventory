using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What /desktop-sales says when its list does not load.
/// </summary>
/// <remarks>
/// It used to say "Check that the API is running" whatever happened, and on 2026-09-27 said it with the API
/// up and healthy — which sent the diagnosis in the wrong direction. Each test here is one of the ways the
/// read can fail, driven through the real service, and pins the sentence the operator now sees for it.
/// </remarks>
public sealed class DesktopSalesLoadFailureTests
{
    private static readonly DesktopSalesQuery Query = new() { IncludeFacets = true };

    [Fact]
    public async Task An_API_error_is_shown_with_its_status_and_the_APIs_own_detail()
    {
        var service = Service(Answer(HttpStatusCode.InternalServerError,
            """{"type":"about:blank","title":"Internal Server Error","status":500,"detail":"column d.PostingDate does not exist"}""",
            "application/problem+json"));

        var (result, error) = await service.ReadDesktopSalesAsync(Query);

        Assert.Null(result);
        Assert.Equal("The API answered 500 Internal Server Error: Column d.PostingDate does not exist.", error);
    }

    [Fact]
    public async Task A_proxy_error_page_is_shown_as_its_status_alone()
    {
        // What IIS answers while an app pool starts or a slot is being swapped: an HTML page with nothing in
        // it worth putting on screen.
        var service = Service(Answer(HttpStatusCode.ServiceUnavailable,
            "<html><body><h1>Service Unavailable</h1></body></html>", "text/html"));

        var (_, error) = await service.ReadDesktopSalesAsync(Query);

        Assert.Equal("The API answered 503 Service Unavailable, with no explanation.", error);
    }

    [Fact]
    public async Task A_reply_the_Web_cannot_read_names_the_field_that_disagreed()
    {
        // The Web hand-mirrors the API's DTOs, and a mismatch used to be indistinguishable from an outage.
        var service = Service(Answer(HttpStatusCode.OK,
            """{"sales":[],"totalCount":"many"}""", "application/json"));

        var (result, error) = await service.ReadDesktopSalesAsync(Query);

        Assert.Null(result);
        Assert.StartsWith("The API answered, but the Web could not read its reply:", error);
        Assert.Contains("$.totalCount", error);
    }

    [Fact]
    public async Task A_slow_API_says_how_long_the_Web_waited()
    {
        var service = Service(new HangingHandler(), TimeSpan.FromMilliseconds(100));

        var (_, error) = await service.ReadDesktopSalesAsync(Query);

        Assert.Equal("The API did not answer within 0.1 seconds.", error);
    }

    [Fact]
    public async Task A_refused_connection_says_the_API_could_not_be_reached()
    {
        // A real refusal, not a scripted one: a port that was just released answers nothing.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var service = new DesktopIntegrationService(client, NullLogger<DesktopIntegrationService>.Instance);

        var (_, error) = await service.ReadDesktopSalesAsync(Query);

        Assert.StartsWith("The Web could not reach the API:", error);
        Assert.Contains($"127.0.0.1:{port}", error);
    }

    [Fact]
    public async Task A_loaded_list_carries_no_error()
    {
        var service = Service(Answer(HttpStatusCode.OK,
            """{"sales":[],"totalCount":0,"page":1,"pageSize":50,"hasMore":false}""", "application/json"));

        var (result, error) = await service.ReadDesktopSalesAsync(Query);

        Assert.NotNull(result);
        Assert.Null(error);
    }

    [Fact]
    public async Task The_null_returning_read_still_returns_null_on_failure()
    {
        // The vending page counts off this form and treats null as "no count"; it has nowhere to show a reason.
        var service = Service(Answer(HttpStatusCode.InternalServerError, "", "text/plain"));

        Assert.Null(await service.GetDesktopSalesAsync(Query));
    }

    private static DesktopIntegrationService Service(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") };
        if (timeout is { } value)
        {
            client.Timeout = value;
        }

        return new DesktopIntegrationService(client, NullLogger<DesktopIntegrationService>.Instance);
    }

    private static HttpMessageHandler Answer(HttpStatusCode status, string body, string mediaType) =>
        new FixedHandler(status, body, mediaType);

    private sealed class FixedHandler(HttpStatusCode status, string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            });
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }
}
