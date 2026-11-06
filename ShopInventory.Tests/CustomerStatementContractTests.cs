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

    /// <summary>
    /// A period that runs past today is aged as at today. A 7-day-terms customer's statement for
    /// 1 Oct – 30 Nov, pulled on 6 Nov, was aged as at 30 Nov, a date that had not happened yet. From
    /// there every October document looked more than three weeks overdue, so the whole -25.29 printed
    /// under "Over 21 Days" and the buckets that held October's activity read 0.00. The open items
    /// below have that account's dates and amounts.
    /// </summary>
    [Fact]
    public async Task A_period_that_ends_after_today_is_aged_as_at_today()
    {
        var clock = new ManualClock();
        // 23:27 UTC on 5 Nov is 01:27 on 6 Nov in Harare, when the PDF was generated.
        clock.Advance(new DateTimeOffset(2026, 11, 5, 23, 27, 0, TimeSpan.Zero) - clock.GetUtcNow());

        var statement = await BuildStatementAsync(
            SevenDays,
            openItems:
            [
                OpenItem("20260909", "20260909", credit: 13.99m),
                OpenItem("20261019", "20261026", debit: 342.94m),
                OpenItem("20261020", "20261020", credit: 11.30m),
                OpenItem("20261021", "20261021", credit: 342.94m),
                OpenItem("20261103", "20261110", debit: 382.59m),
                OpenItem("20261104", "20261104", credit: 382.59m)
            ],
            fromDate: new DateTime(2026, 10, 1),
            toDate: new DateTime(2026, 11, 30),
            clock: clock);

        Assert.Equal(new DateTime(2026, 11, 6), statement.Aging.AsAtDate);
        Assert.Equal(382.59m, statement.Aging.Current);        // invoice due 10 Nov
        Assert.Equal(-382.59m, statement.Aging.Days1To30);     // receipt due 4 Nov
        Assert.Equal(342.94m, statement.Aging.Days31To60);     // invoice due 26 Oct
        Assert.Equal(-354.24m, statement.Aging.Days61To90);    // receipts due 20 and 21 Oct
        Assert.Equal(-13.99m, statement.Aging.Over90Days);     // credit due 9 Sep
        Assert.Equal(-25.29m, statement.Aging.Total);
    }

    /// <summary>
    /// Buckets follow SAP's Customer Receivables Aging report. These four lines are taken from that
    /// report for one customer, aged as at 30 Oct 2026. A receipt ages from the day it was posted, not
    /// a payment-terms later. A line due on the as-at date is already overdue, so the first bucket
    /// starts at 0.
    /// </summary>
    [Fact]
    public async Task Lines_age_from_their_sap_due_date_and_one_due_on_the_day_is_overdue()
    {
        var clock = new ManualClock();
        clock.Advance(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero) - clock.GetUtcNow());

        var statement = await BuildStatementAsync(
            SevenDays,
            openItems:
            [
                OpenItem("20261014", "20261014", credit: 2900.00m),  // receipt 16 days old: SAP's 15-28
                OpenItem("20261022", "20261029", debit: 2866.31m),   // invoice due the day before
                OpenItem("20261030", "20261030", credit: 1800.00m),  // receipt due on the day
                OpenItem("20261024", "20261031", debit: 3664.12m)    // invoice due the day after
            ],
            fromDate: new DateTime(2026, 10, 1),
            toDate: new DateTime(2026, 10, 30),
            clock: clock);

        Assert.Equal("0-7 Days", statement.Aging.Bucket1Label);
        Assert.Equal(3664.12m, statement.Aging.Current);
        Assert.Equal(1066.31m, statement.Aging.Days1To30);
        Assert.Equal(0m, statement.Aging.Days31To60);
        Assert.Equal(-2900.00m, statement.Aging.Days61To90);
    }

    /// <summary>A period that has already closed is still aged as at its own end date.</summary>
    [Fact]
    public async Task A_closed_period_is_aged_as_at_its_end_date_whatever_today_is()
    {
        var clock = new ManualClock();
        clock.Advance(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero) - clock.GetUtcNow());

        var statement = await BuildStatementAsync(
            SevenDays,
            openItems: [OpenItem("20261019", "20261026", debit: 342.94m)],
            fromDate: new DateTime(2026, 10, 1),
            toDate: new DateTime(2026, 10, 31),
            clock: clock);

        // Due 26 Oct, five days overdue at 31 Oct. Measured from 6 Nov it would be eleven.
        Assert.Equal(new DateTime(2026, 10, 31), statement.Aging.AsAtDate);
        Assert.Equal(342.94m, statement.Aging.Days1To30);
        Assert.Equal(0m, statement.Aging.Days31To60);
    }

    private static readonly PaymentTermsDto SevenDays = new()
    {
        GroupNumber = 1,
        PaymentTermsGroupName = "7DAYS",
        NumberOfAdditionalMonths = 0,
        NumberOfAdditionalDays = 7
    };

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
        List<Dictionary<string, object?>>? openItems = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        TimeProvider? clock = null)
    {
        var handler = new GetCustomerStatementHandler(
            BusinessPartners("ABS006", "Absolute Refregiration", payTermGrpCode: paymentTerms?.GroupNumber),
            Sap(paymentTerms, openItems ?? []),
            StatementBuildCaches.Fresh(),
            NullLogger<GetCustomerStatementHandler>.Instance,
            clock);

        var result = await handler.Handle(
            new GetCustomerStatementQuery(
                "ABS006",
                fromDate ?? new DateTime(2026, 5, 1),
                toDate ?? new DateTime(2026, 5, 31),
                null),
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
            nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryWithBudgetAsync) => LedgerRows((string)args![0]!, openItems),
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
