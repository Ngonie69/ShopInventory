using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WebCreditNoteService = ShopInventory.Web.Services.CreditNoteService;
using WebLineRequest = ShopInventory.Web.Models.CreateCreditNoteLineRequest;

namespace ShopInventory.Tests;

/// <summary>
/// What the operator reads when the API refuses a credit note raised from an invoice.
/// </summary>
/// <remarks>
/// The Web read only a <c>message</c> property from the refusal, and the API has not answered with one
/// since it moved to problem+json: the reason is in <c>errors</c> and <c>errorDetails</c>. Every refusal
/// therefore reached the screen as the same "Failed to create credit note." — SAP saying no, an invoice
/// already credited, a credit history that could not be read — and nobody at the counter could tell
/// which, or what to do about it.
/// </remarks>
public sealed class CreditNoteFromInvoiceRefusalMessageTests
{
    /// <summary>
    /// The body <c>ApiControllerBase.Problem</c> writes for <c>Errors.CreditNote.SapRejected</c>, as it
    /// goes over the wire (camelCase, extensions flattened).
    /// </summary>
    private const string SapRefusalBody = """
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
          "title": "One or more validation errors occurred.",
          "status": 400,
          "detail": "The request contains validation errors.",
          "errors": {
            "CreditNote.SapRejected": [
              "SAP refused the credit note: No matching records found (ODBC -2028)"
            ]
          },
          "code": "CreditNote.SapRejected",
          "errorDetails": [
            {
              "code": "CreditNote.SapRejected",
              "description": "SAP refused the credit note: No matching records found (ODBC -2028)",
              "type": "Validation"
            }
          ],
          "traceId": "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"
        }
        """;

    /// <summary>The service's own refusal: <c>Errors.CreditNote.InvalidOperation</c>.</summary>
    private const string AlreadyCreditedBody = """
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
          "title": "One or more validation errors occurred.",
          "status": 400,
          "detail": "The request contains validation errors.",
          "errors": {
            "CreditNote.InvalidOperation": [
              "Invoice #772109 has already been fully credited. No additional credit note can be created."
            ]
          },
          "code": "CreditNote.InvalidOperation",
          "errorDetails": [
            {
              "code": "CreditNote.InvalidOperation",
              "description": "Invoice #772109 has already been fully credited. No additional credit note can be created.",
              "type": "Validation"
            }
          ]
        }
        """;

    [Fact]
    public async Task SAPs_refusal_reaches_the_operator()
    {
        var result = await CreateAgainst(HttpStatusCode.BadRequest, SapRefusalBody);

        Assert.False(result.Success);
        Assert.Contains("No matching records found (ODBC -2028)", result.ErrorMessage);
    }

    [Fact]
    public async Task The_services_own_refusal_reaches_the_operator()
    {
        var result = await CreateAgainst(HttpStatusCode.BadRequest, AlreadyCreditedBody);

        Assert.False(result.Success);
        Assert.Contains("has already been fully credited", result.ErrorMessage);
    }

    [Fact]
    public async Task A_body_with_nothing_to_say_still_falls_back_to_the_generic_sentence()
    {
        var result = await CreateAgainst(HttpStatusCode.InternalServerError, "");

        Assert.False(result.Success);
        Assert.Equal("Failed to create credit note.", result.ErrorMessage);
    }

    private static Task<ShopInventory.Web.Models.CreateCreditNoteResult> CreateAgainst(
        HttpStatusCode status, string body)
    {
        var client = new HttpClient(new StubHandler(status, body))
        {
            BaseAddress = new Uri("http://api.test/")
        };

        var service = new WebCreditNoteService(client, NullLogger<WebCreditNoteService>.Instance);

        return service.CreateFromInvoiceAsync(
            2342939,
            [new WebLineRequest { ItemCode = "ICS025", Quantity = 17m, UnitPrice = 6.25m, OriginalInvoiceLineId = 0 }],
            "Customer changed order",
            "4f7b2c1a9e8d0f3b6a5c4d2e1f0a9b8c");
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/problem+json")
            });
    }
}
