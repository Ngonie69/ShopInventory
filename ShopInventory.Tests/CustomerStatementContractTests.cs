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

    /// <summary>
    /// Aging is built from unreconciled journal lines, so a credit the customer is holding has to
    /// pull it down. Built from invoices alone it could only climb, and ABS006's July statement
    /// printed 41,275.73 due directly above a closing balance of 24,875.40 — the receipts sitting
    /// unapplied and the two invoices already closed by credit notes had nothing to subtract them.
    /// </summary>
    [Fact]
    public async Task Aging_nets_open_credits_off_and_totals_to_the_closing_balance()
    {
        var statement = await BuildStatementAsync(
            paymentTerms: null,
            openItems: [OpenItem("20260506", "20260506", debit: 1400m), OpenItem("20260507", "20260507", credit: 250m)]);

        Assert.Equal(1150m, statement.ClosingBalance);
        Assert.Equal(statement.ClosingBalance, statement.Aging.Total);
        Assert.Equal(1150m, statement.Aging.Days1To30);
    }

    /// <summary>
    /// Aging speaks for the statement's end date, not for the day the PDF was generated. Buckets
    /// used to be measured from <c>DateTime.Today</c>, so re-downloading a closed period aged every
    /// document in it further each day that passed.
    /// </summary>
    [Fact]
    public async Task Aging_is_measured_from_the_statement_end_date_rather_than_today()
    {
        var statement = await BuildStatementAsync(
            paymentTerms: null,
            openItems: [OpenItem("20260506", "20260506", debit: 1400m)]);

        // 25 days before the 31 May end date. Measured from any real "today" it would be long past
        // every bucket and land in Over90Days instead.
        Assert.Equal(1400m, statement.Aging.Days1To30);
        Assert.Equal(0m, statement.Aging.Over90Days);
    }

    /// <summary>An open item SAP has not reconciled, in the shape the open-items query returns.</summary>
    private static Dictionary<string, object?> OpenItem(
        string postingDate,
        string dueDate,
        decimal debit = 0m,
        decimal credit = 0m) =>
        new()
        {
            ["PostingDate"] = postingDate,
            ["DueDate"] = dueDate,
            ["BalanceDueDebit"] = debit,
            ["BalanceDueCredit"] = credit
        };

    private static async Task<CustomerStatementResponseDto> BuildStatementAsync(
        PaymentTermsDto? paymentTerms,
        List<Dictionary<string, object?>>? openItems = null)
    {
        var handler = new GetCustomerStatementHandler(
            BusinessPartners("ABS006", "Absolute Refregiration", payTermGrpCode: paymentTerms?.GroupNumber),
            Sap(paymentTerms, openItems ?? []),
            StatementBuildCaches.Fresh(),
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

    /// <remarks>
    /// <c>GetOpenInvoicesByCustomersAsync</c> is deliberately absent. Aging no longer reads invoices,
    /// and the catch-all below turns reintroducing that read back into a failing test rather than a
    /// statement whose aging quietly stops agreeing with its own balance.
    /// </remarks>
    private static ISAPServiceLayerClient Sap(
        PaymentTermsDto? paymentTerms,
        List<Dictionary<string, object?>> openItems) =>
        StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetPaymentTermsByCodeAsync) => Task.FromResult(paymentTerms),
            nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryAsync) => LedgerRows((string)args![0]!, openItems),
            _ => throw new InvalidOperationException($"ISAPServiceLayerClient.{method.Name} was not expected.")
        });

    /// <summary>Answers the three statement queries by their fixed code, the way SAP would.</summary>
    private static Task<List<Dictionary<string, object?>>> LedgerRows(
        string queryCode,
        List<Dictionary<string, object?>> openItems) =>
        Task.FromResult(queryCode switch
        {
            "STMT_OPEN_ITEMS" => openItems,
            "STMT_OPENING_BALANCE" =>
            [
                new Dictionary<string, object?> { ["TotalDebit"] = 3000m, ["TotalCredit"] = 2000m }
            ],
            "STMT_LEDGER_ROWS" =>
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
