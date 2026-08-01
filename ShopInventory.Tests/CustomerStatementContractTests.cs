using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Features.Statements.Queries.GetCustomerStatement;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the wire shape of the customer statement against the web portal, which mirrors
/// <see cref="CustomerStatementResponseDto"/> with its own model and reads it with
/// <c>System.Text.Json</c>.
/// </summary>
/// <remarks>
/// This is the contract that broke statements. The handler emitted <c>"paymentTermsDays": null</c>
/// whenever SAP would not resolve a customer's payment terms group — a lead, or an account pointing
/// at a group that no longer exists — and the portal's mirror of this DTO declares that field a
/// plain <c>int</c>. <c>System.Text.Json</c> throws on <c>null</c> into a non-nullable value type
/// rather than defaulting, so the deserialisation of the whole response failed, the page showed an
/// error, and Download PDF stayed disabled because it is gated on a loaded statement. One absent
/// payment-terms group took the entire feature down for that customer.
///
/// The test asserts against a local copy of the portal's model rather than the real one because the
/// two live in different assemblies and this project references only the API. The copy is the point:
/// it fails if the API ever reintroduces a null a non-nullable consumer cannot read.
/// </remarks>
public class CustomerStatementContractTests
{
    /// <summary>A stand-in for ShopInventory.Web's <c>CustomerInfo</c>, non-nullable field and all.</summary>
    private sealed class PortalCustomerInfo
    {
        public string CardCode { get; set; } = string.Empty;
        public string CardName { get; set; } = string.Empty;
        public decimal Balance { get; set; }
        public string? Currency { get; set; }
        public string AccountStructure { get; set; } = "Single";
        public string? PaymentTermsName { get; set; }
        public int PaymentTermsDays { get; set; }
    }

    private sealed class PortalStatementResponse
    {
        public PortalCustomerInfo Customer { get; set; } = new();
        public decimal OpeningBalance { get; set; }
        public decimal ClosingBalance { get; set; }
    }

    [Fact]
    public async Task A_customer_with_no_payment_terms_still_produces_a_statement_the_portal_can_read()
    {
        var statement = await BuildStatementAsync(paymentTerms: null);

        var json = JsonSerializer.Serialize(statement, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // The failure mode was a throw here, not a wrong value.
        var portalView = JsonSerializer.Deserialize<PortalStatementResponse>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(portalView);
        Assert.Equal(0, portalView!.Customer.PaymentTermsDays);
        Assert.Equal("ABS006", portalView.Customer.CardCode);
    }

    [Fact]
    public async Task Payment_terms_are_reported_in_days()
    {
        var statement = await BuildStatementAsync(new PaymentTermsDto
        {
            GroupNumber = 3,
            PaymentTermsGroupName = "30 DAYS",
            NumberOfAdditionalMonths = 1,
            NumberOfAdditionalDays = 0
        });

        Assert.Equal(30, statement.Customer.PaymentTermsDays);
        Assert.Equal("30 DAYS", statement.Customer.PaymentTermsName);
    }

    [Fact]
    public async Task The_ledger_carries_a_running_balance_from_the_opening_balance()
    {
        var statement = await BuildStatementAsync(paymentTerms: null);

        Assert.Equal(1000m, statement.OpeningBalance);
        Assert.Collection(
            statement.Lines,
            invoice =>
            {
                Assert.Equal("IN", invoice.OriginCode);
                Assert.Equal(1400m, invoice.Balance);
            },
            payment =>
            {
                Assert.Equal("RC", payment.OriginCode);
                Assert.Equal(1150m, payment.Balance);
            });
        Assert.Equal(400m, statement.TotalDebits);
        Assert.Equal(250m, statement.TotalCredits);
        Assert.Equal(1150m, statement.ClosingBalance);
    }

    private static async Task<CustomerStatementResponseDto> BuildStatementAsync(PaymentTermsDto? paymentTerms)
    {
        var handler = new GetCustomerStatementHandler(
            BusinessPartners("ABS006", "Absolute Refregiration", payTermGrpCode: paymentTerms?.GroupNumber),
            Sap(paymentTerms),
            NullLogger<GetCustomerStatementHandler>.Instance);

        var result = await handler.Handle(
            new GetCustomerStatementQuery("ABS006", new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), null),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        return result.Value;
    }

    private static IBusinessPartnerService BusinessPartners(string cardCode, string cardName, int? payTermGrpCode) =>
        StubProxy.For<IBusinessPartnerService>((method, _) => method.Name switch
        {
            nameof(IBusinessPartnerService.GetBusinessPartnerByCodeAsync) => Task.FromResult<BusinessPartnerDto?>(
                new BusinessPartnerDto
                {
                    CardCode = cardCode,
                    CardName = cardName,
                    CardType = "L",
                    Currency = "USD",
                    Balance = 1150m,
                    PayTermGrpCode = payTermGrpCode
                }),
            _ => throw new InvalidOperationException($"IBusinessPartnerService.{method.Name} was not expected.")
        });

    private static ISAPServiceLayerClient Sap(PaymentTermsDto? paymentTerms) =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetPaymentTermsByCodeAsync) => Task.FromResult(paymentTerms),
            nameof(ISAPServiceLayerClient.ExecuteScopedRawSqlQueryAsync) => LedgerRows((string)args![0]!),
            nameof(ISAPServiceLayerClient.GetOpenInvoicesByCustomersAsync) => Task.FromResult(new List<Models.Invoice>()),
            _ => throw new InvalidOperationException($"ISAPServiceLayerClient.{method.Name} was not expected.")
        });

    /// <summary>Answers the two statement queries by their code prefix, the way SAP would.</summary>
    private static Task<List<Dictionary<string, object?>>> LedgerRows(string queryCodePrefix) =>
        Task.FromResult(queryCodePrefix switch
        {
            "StmtOpen" =>
            [
                new Dictionary<string, object?> { ["TotalDebit"] = 3000m, ["TotalCredit"] = 2000m }
            ],
            "StmtRows" =>
            [
                new Dictionary<string, object?>
                {
                    ["PostingDate"] = "2026-05-06",
                    ["TransactionNumber"] = 501,
                    ["TransType"] = 13,
                    ["OriginNumber"] = "84565",
                    ["Details"] = "Absolute Refregiration",
                    ["Debit"] = 400m,
                    ["Credit"] = 0m,
                    ["LineId"] = 0
                },
                new Dictionary<string, object?>
                {
                    ["PostingDate"] = "2026-05-07",
                    ["TransactionNumber"] = 502,
                    ["TransType"] = 24,
                    ["OriginNumber"] = "84606",
                    ["Details"] = "Absolute Refregiration-account",
                    ["Debit"] = 0m,
                    ["Credit"] = 250m,
                    ["LineId"] = 0
                }
            ],
            _ => new List<Dictionary<string, object?>>()
        });
}
