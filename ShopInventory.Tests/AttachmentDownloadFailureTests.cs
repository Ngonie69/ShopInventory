using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What the person who clicked View or Download is told when the file does not arrive.
/// </summary>
/// <remarks>
/// The SAP Service Layer in this landscape can serve no attachment at all — every
/// <c>Attachments2(n)/$value</c> answers <c>404 Fail to get the LINUX mount point for
/// AttachmentsFolderPath</c> — so the API's refusal carries the only sentence anybody can act on.
/// It used to stop at the proxy, which returned a bare status code, and the page said "The
/// attachment could not be opened." to somebody looking straight at the file's name.
/// </remarks>
public sealed class AttachmentDownloadFailureTests
{
    private const string Refusal =
        "The attachment could not be read from SAP: 'brian 01_09_26.pdf' is not in the SAP attachments folder.";

    [Fact]
    public async Task The_proxy_passes_the_APIs_refusal_through_to_the_browser()
    {
        var problem = $$"""
            {"type":"about:blank","title":"{{Refusal}}","status":400,"detail":"{{Refusal}}","code":"CreditNoteApproval.AttachmentUnavailable"}
            """;
        var (proxy, httpContext) = CreateProxy(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(problem, Encoding.UTF8, "application/problem+json")
        });

        var result = await proxy.ProxyAsync(
            httpContext,
            "api/credit-note-approvals/84752/attachments/1/download",
            "credit-note-draft-84752-1",
            ["Admin", "Manager"],
            CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(400, httpContext.Response.StatusCode);
        Assert.Contains("application/problem+json", httpContext.Response.ContentType);
        Assert.Contains(Refusal, await ReadBodyAsync(httpContext), StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal with nothing in it is still a refusal: the status has to survive even when there is
    /// no sentence to forward, or the browser would read an empty 200 as the file itself.
    /// </summary>
    [Fact]
    public async Task An_empty_refusal_still_answers_its_own_status()
    {
        var (proxy, httpContext) = CreateProxy(new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await proxy.ProxyAsync(
            httpContext, "api/credit-note-approvals/84752/attachments/9/download", "file", ["Admin"], CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(404, httpContext.Response.StatusCode);
        Assert.Empty(await ReadBodyAsync(httpContext));
    }

    [Fact]
    public async Task A_file_that_does_arrive_is_streamed_as_before()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 credit note");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pdf) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        var (proxy, httpContext) = CreateProxy(response);

        var result = await proxy.ProxyAsync(
            httpContext, "api/credit-note-approvals/84752/attachments/1/download", "file.pdf", ["Admin"], CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(200, httpContext.Response.StatusCode);
        Assert.Equal("application/pdf", httpContext.Response.ContentType);
        Assert.Equal(pdf, ((MemoryStream)httpContext.Response.Body).ToArray());
    }

    /// <summary>
    /// Blazor hands the .NET side the JavaScript error's message and its stack as one string. The
    /// page shows this on a snackbar, so everything after the first line has to go.
    /// </summary>
    [Fact]
    public void The_page_reads_the_message_a_browser_helper_threw()
    {
        var jsException = new JSException(
            $"{Refusal}\nError: {Refusal}\n    at fetchAuthenticatedBlob (app.js:451:15)\n    at async downloadAuthenticatedFile (app.js:480:20)");

        Assert.Equal(Refusal, JsInteropErrors.DescribeOrDefault(jsException, "The attachment could not be opened."));
    }

    [Fact]
    public void A_failure_that_did_not_come_from_the_browser_keeps_the_pages_own_sentence()
    {
        // JSDisconnectedException and the like carry framework text that means nothing to a manager
        // looking at a credit note.
        Assert.Equal(
            "The attachment could not be opened.",
            JsInteropErrors.DescribeOrDefault(
                new InvalidOperationException("The circuit failed to register"), "The attachment could not be opened."));
    }

    [Fact]
    public void An_empty_javascript_message_keeps_the_pages_own_sentence()
    {
        Assert.Equal(
            "The attachment could not be downloaded.",
            JsInteropErrors.DescribeOrDefault(new JSException("   \n at app.js"), "The attachment could not be downloaded."));
    }

    [Fact]
    public void A_message_too_long_for_a_snackbar_is_cut_rather_than_pasted_whole()
    {
        var described = JsInteropErrors.DescribeOrDefault(new JSException(new string('x', 5_000)), "fallback");

        Assert.True(described.Length < 400, $"a snackbar message ran to {described.Length} characters");
        Assert.EndsWith("…", described, StringComparison.Ordinal);
    }

    private static (AuthenticatedDownloadProxy Proxy, HttpContext Context) CreateProxy(HttpResponseMessage apiResponse)
    {
        var proxy = new AuthenticatedDownloadProxy(
            new StubHttpClientFactory(apiResponse),
            NullLogger<AuthenticatedDownloadProxy>.Instance);

        var services = new ServiceCollection();
        services.AddLogging();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "manager"), new Claim(ClaimTypes.Role, "Admin")],
                authenticationType: "Test"))
        };
        httpContext.Request.Headers.Authorization = "Bearer not-a-real-token";
        httpContext.Response.Body = new MemoryStream();

        return (proxy, httpContext);
    }

    private static async Task<string> ReadBodyAsync(HttpContext httpContext)
    {
        httpContext.Response.Body.Position = 0;
        return await new StreamReader(httpContext.Response.Body).ReadToEndAsync();
    }

    private sealed class StubHttpClientFactory(HttpResponseMessage response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHandler(response)) { BaseAddress = new Uri("http://localhost:5106/") };

        private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                response.RequestMessage = request;
                return Task.FromResult(response);
            }
        }
    }
}
