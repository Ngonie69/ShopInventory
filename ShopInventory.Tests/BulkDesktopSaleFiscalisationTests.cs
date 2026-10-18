using System.Net;
using System.Text;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The desktop sales console's bulk "Retry fiscalisation", as the Web sends it: the per-sale endpoint
/// once per selected sale, in order, with a row per sale.
/// </summary>
public sealed class BulkDesktopSaleFiscalisationTests
{
    private static string Fiscalised(string reference, string receipt) => $$"""
        {
          "externalReferenceId": "{{reference}}",
          "outcome": "Fiscalised",
          "fiscalReceiptNumber": "{{receipt}}",
          "message": "Fiscalised as receipt {{receipt}}. It will post to SAP with the next pass."
        }
        """;

    /// <summary>What <c>ApiControllerBase.Problem</c> writes for <c>SaleNotFiscalisable</c>, a validation error.</summary>
    private static string NotFiscalisable(string reference) => $$"""
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
          "title": "One or more validation errors occurred.",
          "status": 400,
          "detail": "The request contains validation errors.",
          "errors": { "DesktopSales.SaleNotFiscalisable": [ "{{reference}}: This sale is already fiscalised." ] },
          "code": "DesktopSales.SaleNotFiscalisable"
        }
        """;

    private static string Failure(string code, string reference, string detail) => $$"""
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
          "title": "{{reference}}: {{detail}}",
          "status": 400,
          "detail": "{{reference}}: {{detail}}",
          "code": "{{code}}"
        }
        """;

    private static DesktopSaleFiscalisationTarget Till(string reference, string warehouse = "HRE01") =>
        new(reference, "ShopTill", warehouse);

    [Fact]
    public async Task Every_sale_gets_a_row_in_the_order_it_was_sent()
    {
        var handler = new StubHandler(new()
        {
            ["A"] = (HttpStatusCode.OK, Fiscalised("A", "101")),
            ["B"] = (HttpStatusCode.BadRequest, Failure("DesktopSales.SaleFiscalisationFailed", "B", "ITEMNAME2 refused")),
            ["C"] = (HttpStatusCode.BadRequest, NotFiscalisable("C")),
        });

        var result = await Service(handler).RetrySalesFiscalisationAsync([Till("A"), Till("B"), Till("C")]);

        Assert.Equal(["A", "B", "C"], handler.Sent);
        Assert.Equal(1, result.WithReceipt);

        Assert.Equal(DesktopSaleFiscalisationRowOutcomes.Fiscalised, result.Results[0].Outcome);
        Assert.Equal("101", result.Results[0].FiscalReceiptNumber);

        Assert.Equal(DesktopSaleFiscalisationRowOutcomes.Failed, result.Results[1].Outcome);
        Assert.Equal("ITEMNAME2 refused", result.Results[1].Message);

        // The reason, not the validation boilerplate, and without the reference the table already shows.
        Assert.Equal(DesktopSaleFiscalisationRowOutcomes.Refused, result.Results[2].Outcome);
        Assert.Equal("This sale is already fiscalised.", result.Results[2].Message);
    }

    [Fact]
    public async Task A_device_that_cannot_be_asked_stops_only_its_own_sales()
    {
        var handler = new StubHandler(new()
        {
            ["A"] = (HttpStatusCode.BadRequest, Failure("DesktopSales.SaleFiscalisationUncheckable", "A", "Nothing was sent")),
            ["B"] = (HttpStatusCode.OK, Fiscalised("B", "102")),
            ["C"] = (HttpStatusCode.OK, Fiscalised("C", "103")),
        });

        var result = await Service(handler).RetrySalesFiscalisationAsync(
            [Till("A", "HRE01"), Till("B", "BYO01"), Till("C", "hre01")]);

        Assert.Equal(["A", "B"], handler.Sent);
        Assert.Equal(DesktopSaleFiscalisationRowOutcomes.Unreachable, result.Results[0].Outcome);
        Assert.Equal(DesktopSaleFiscalisationRowOutcomes.Fiscalised, result.Results[1].Outcome);
        Assert.Equal(DesktopSaleFiscalisationRowOutcomes.NotSent, result.Results[2].Outcome);
    }

    [Fact]
    public async Task Progress_counts_each_answer()
    {
        var handler = new StubHandler(new()
        {
            ["A"] = (HttpStatusCode.OK, Fiscalised("A", "101")),
            ["B"] = (HttpStatusCode.OK, Fiscalised("B", "102")),
        });
        var reported = new List<int>();

        await Service(handler).RetrySalesFiscalisationAsync(
            [Till("A"), Till("B")], new SynchronousProgress(reported.Add));

        Assert.Equal([1, 2], reported);
    }

    [Fact]
    public async Task A_closed_page_sends_nothing_more()
    {
        var handler = new StubHandler(new() { ["A"] = (HttpStatusCode.OK, Fiscalised("A", "101")) });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var result = await Service(handler).RetrySalesFiscalisationAsync([Till("A")], cancellationToken: cancelled.Token);

        Assert.Empty(handler.Sent);
        Assert.Equal(DesktopSaleFiscalisationRowOutcomes.NotSent, Assert.Single(result.Results).Outcome);
    }

    [Fact]
    public async Task The_drawer_s_single_retry_shows_the_refusal_s_reason()
    {
        var handler = new StubHandler(new() { ["A"] = (HttpStatusCode.BadRequest, NotFiscalisable("A")) });

        var (result, error) = await Service(handler).RetrySaleFiscalisationAsync("A");

        Assert.Null(result);
        Assert.Equal("A: This sale is already fiscalised.", error);
    }

    private static DesktopIntegrationService Service(StubHandler handler) =>
        SignedOutBrowser.DesktopIntegration(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") });

    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private sealed class StubHandler(Dictionary<string, (HttpStatusCode Status, string Body)> answers) : HttpMessageHandler
    {
        public List<string> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // /api/DesktopIntegration/sales/{reference}/fiscalise
            var segments = request.RequestUri!.AbsolutePath.Split('/');
            Assert.Equal("fiscalise", segments[^1]);
            var reference = Uri.UnescapeDataString(segments[^2]);
            Sent.Add(reference);

            var (status, body) = answers[reference];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
