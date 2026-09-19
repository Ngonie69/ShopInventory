using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The desktop sales console's Consolidate button, as the Web sends it.
/// </summary>
/// <remarks>
/// The button used to send no date, so the API closed its own today whatever day the console showed.
/// Sales left over from yesterday could not be reached from there, and "nothing pending today" came
/// back as a 400 that the Web turned into a bool and showed as "The consolidation run could not be
/// started." On 18 Sep 2026 that was all an operator with 36 unposted sales got to read.
/// </remarks>
public sealed class ConsolidationButtonDateTests
{
    /// <summary>What <c>ApiControllerBase.Problem</c> writes for <c>Errors.DesktopSales.NoPendingSales</c>.</summary>
    private const string NothingPendingBody = """
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
          "title": "Nothing on 18 Sep 2026 is waiting for consolidation. Till, vending and van sales are not consolidated: they post to SAP one invoice each, so select them in the list and use Post to SAP.",
          "status": 400,
          "detail": "Nothing on 18 Sep 2026 is waiting for consolidation. Till, vending and van sales are not consolidated: they post to SAP one invoice each, so select them in the list and use Post to SAP.",
          "code": "DesktopSales.NoPendingSales"
        }
        """;

    private const string ConsolidatedBody = """
        {
          "consolidationDate": "2026-09-18T00:00:00",
          "totalSalesProcessed": 7,
          "successfulPostings": 3,
          "failedPostings": 1,
          "groups": []
        }
        """;

    [Fact]
    public async Task The_day_on_screen_is_sent_as_a_bare_date()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ConsolidatedBody);

        await Service(handler).TriggerConsolidationAsync(new DateTime(2026, 9, 18, 23, 30, 0, DateTimeKind.Local));

        Assert.Equal("/api/DesktopIntegration/end-of-day/consolidate", handler.Path);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("2026-09-18", body.RootElement.GetProperty("consolidationDate").GetString());
    }

    [Fact]
    public async Task The_run_s_counts_come_back_to_the_page()
    {
        var (result, error) = await Service(new StubHandler(HttpStatusCode.OK, ConsolidatedBody))
            .TriggerConsolidationAsync(new DateTime(2026, 9, 18));

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(7, result.TotalSalesProcessed);
        Assert.Equal(3, result.SuccessfulPostings);
        Assert.Equal(1, result.FailedPostings);
    }

    [Fact]
    public async Task Nothing_to_consolidate_reaches_the_operator_in_the_API_s_words()
    {
        var (result, error) = await Service(new StubHandler(HttpStatusCode.BadRequest, NothingPendingBody))
            .TriggerConsolidationAsync(new DateTime(2026, 9, 18));

        Assert.Null(result);
        Assert.Contains("18 Sep 2026", error);
        Assert.Contains("Post to SAP", error);
    }

    private static DesktopIntegrationService Service(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") },
            NullLogger<DesktopIntegrationService>.Instance);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/problem+json")
            };
        }
    }
}
