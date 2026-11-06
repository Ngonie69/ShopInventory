using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.Statements;
using ShopInventory.Features.Statements.Queries.GetCustomerStatement;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What a customer statement does when SAP does not answer one of its reads.
/// </summary>
/// <remarks>
/// Measured in production in early November 2026: the reads answer in under a second almost every
/// time, but at least two in twelve sat out the SAP client's whole five-minute timeout. The portal
/// waits the same five minutes, so the customer got a spinner and then nothing. Each read now has
/// a budget of its own and one retry, a statement that cannot be built says so at once, and a
/// statement whose aging could not be read says that rather than printing zeros.
/// </remarks>
public sealed class CustomerStatementStuckReadTests
{
    private const string Card = "C100";

    [Fact]
    public async Task A_ledger_read_that_gets_no_answer_once_is_retried_and_the_statement_is_built()
    {
        var sap = new ScriptedSap { LedgerTimeouts = 1 };

        var result = await NewHandler(sap).Handle(Query(), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.Equal(2, sap.Attempts(GetCustomerStatementHandler.LedgerQueryCode));
        Assert.Single(result.Value.Lines);
        Assert.True(result.Value.AgingAvailable);
    }

    [Fact]
    public async Task A_ledger_read_that_never_answers_fails_the_statement_with_a_plain_message()
    {
        var sap = new ScriptedSap { LedgerTimeouts = int.MaxValue };

        var result = await NewHandler(sap).Handle(Query(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Statement.SapNotAnswering", result.FirstError.Code);
        // Not "still being prepared": nothing is running and nothing was kept.
        Assert.DoesNotContain("still being", result.FirstError.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, sap.Attempts(GetCustomerStatementHandler.LedgerQueryCode));
    }

    [Fact]
    public async Task An_opening_balance_read_that_never_answers_fails_the_statement()
    {
        var sap = new ScriptedSap { OpeningTimeouts = int.MaxValue };

        var result = await NewHandler(sap).Handle(Query(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Statement.SapNotAnswering", result.FirstError.Code);
    }

    [Fact]
    public async Task An_aging_read_that_never_answers_gives_a_statement_marked_aging_unavailable()
    {
        var sap = new ScriptedSap { OpenItemsTimeouts = int.MaxValue };

        var result = await NewHandler(sap).Handle(Query(), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        var statement = result.Value;
        Assert.False(statement.AgingAvailable);
        Assert.Equal(2, sap.Attempts(GetCustomerStatementHandler.OpenItemsQueryCode));

        // Everything but the aging is the customer's real statement.
        Assert.Single(statement.Lines);
        Assert.Equal(1000m, statement.OpeningBalance);
        Assert.Equal(1400m, statement.ClosingBalance);
    }

    [Fact]
    public async Task Aging_that_was_read_is_marked_available_even_when_nothing_is_open()
    {
        // An empty open-items answer is a customer who owes nothing, not a failed read.
        var sap = new ScriptedSap { OpenItems = [] };

        var result = await NewHandler(sap).Handle(Query(), CancellationToken.None);

        Assert.True(result.Value.AgingAvailable);
        Assert.Equal(0m, result.Value.Aging.Total);
    }

    [Fact]
    public async Task A_statement_without_its_aging_is_not_kept_so_generating_it_again_retries_the_aging()
    {
        var sap = new ScriptedSap { OpenItemsTimeouts = int.MaxValue };
        var handler = NewHandler(sap, StatementBuildCaches.Fresh());

        await handler.Handle(Query(), CancellationToken.None);
        await handler.Handle(Query(), CancellationToken.None);

        Assert.Equal(2, sap.Attempts(GetCustomerStatementHandler.LedgerQueryCode));
    }

    [Fact]
    public async Task A_complete_statement_is_still_kept()
    {
        var sap = new ScriptedSap();
        var handler = NewHandler(sap, StatementBuildCaches.Fresh());

        await handler.Handle(Query(), CancellationToken.None);
        await handler.Handle(Query(), CancellationToken.None);

        Assert.Equal(1, sap.Attempts(GetCustomerStatementHandler.LedgerQueryCode));
    }

    [Fact]
    public async Task Every_read_is_given_the_configured_budget_and_attempts()
    {
        var sap = new ScriptedSap { LedgerTimeouts = int.MaxValue };
        var settings = Options.Create(new SAPSettings
        {
            StatementSqlRequestTimeoutSeconds = 12,
            StatementSqlMaxAttempts = 3
        });

        await NewHandler(sap, sapSettings: settings).Handle(Query(), CancellationToken.None);

        Assert.Equal(3, sap.Attempts(GetCustomerStatementHandler.LedgerQueryCode));
        Assert.All(sap.Budgets, budget => Assert.Equal(TimeSpan.FromSeconds(12), budget));
    }

    [Fact]
    public async Task The_default_budget_is_thirty_seconds_and_two_attempts()
    {
        var sap = new ScriptedSap { LedgerTimeouts = int.MaxValue };

        await NewHandler(sap).Handle(Query(), CancellationToken.None);

        Assert.Equal(2, sap.Attempts(GetCustomerStatementHandler.LedgerQueryCode));
        Assert.All(sap.Budgets, budget => Assert.Equal(TimeSpan.FromSeconds(30), budget));
    }

    [Fact]
    public async Task A_read_that_hits_the_sap_clients_own_timeout_is_reported_as_sap_not_answering()
    {
        // The payment terms read has no budget of its own, so the SAP client's five-minute timeout
        // is its limit. That surfaces as a TaskCanceledException, which used to be logged and
        // answered as the build running out of its ten-minute ceiling.
        var sap = new ScriptedSap { PaymentTermsHitClientTimeout = true };

        var result = await NewHandler(sap).Handle(Query(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Statement.SapNotAnswering", result.FirstError.Code);
    }

    [Fact]
    public async Task The_retry_and_give_up_lines_keep_the_wording_the_measuring_script_counts()
    {
        // scripts/Measure-StatementBuildTimes.ps1 finds these lines by their text in the production
        // log. Reword them and the script quietly counts nothing.
        var sap = new ScriptedSap { LedgerTimeouts = int.MaxValue };
        var logger = new CapturingLogger<GetCustomerStatementHandler>();

        await NewHandler(sap, logger: logger).Handle(Query(), CancellationToken.None);

        var messages = logger.Entries.Select(entry => entry.Message).ToList();
        Assert.Contains(
            "Statement leg ledger rows for C100 got no answer from SAP within 30s (attempt 1 of 2); retrying",
            messages);
        Assert.Contains(
            "Statement leg ledger rows for C100 got no answer from SAP in 2 attempt(s) of 30s; giving up",
            messages);
        Assert.Contains(messages, message => message.StartsWith("Customer statement for C100 gave up: ", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.StartsWith("Statement leg ledger rows for C100 finished in ", StringComparison.Ordinal));
    }

    private static GetCustomerStatementQuery Query() =>
        new(Card, new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), null);

    private static GetCustomerStatementHandler NewHandler(
        ScriptedSap sap,
        IStatementBuildCache? cache = null,
        IOptions<SAPSettings>? sapSettings = null,
        CapturingLogger<GetCustomerStatementHandler>? logger = null) =>
        new(
            BusinessPartner(),
            sap.Client,
            cache ?? StatementBuildCaches.Fresh(),
            logger ?? (Microsoft.Extensions.Logging.ILogger<GetCustomerStatementHandler>)NullLogger<GetCustomerStatementHandler>.Instance,
            clock: null,
            sapSettings: sapSettings);

    private static IBusinessPartnerService BusinessPartner() =>
        StubProxy.For<IBusinessPartnerService>((method, _) => method.Name switch
        {
            nameof(IBusinessPartnerService.GetBusinessPartnerByCodeAsync) => Task.FromResult<BusinessPartnerDto?>(
                new BusinessPartnerDto { CardCode = Card, CardName = "Customer", Currency = "USD", PayTermGrpCode = 3 }),
            _ => throw new InvalidOperationException(method.Name)
        });

    /// <summary>
    /// Answers the three statement reads by their fixed code, failing the first N attempts at each
    /// the way the SAP client does when a read runs out of its budget.
    /// </summary>
    private sealed class ScriptedSap
    {
        private readonly ConcurrentDictionary<string, int> _attempts = new();

        public int LedgerTimeouts { get; init; }
        public int OpeningTimeouts { get; init; }
        public int OpenItemsTimeouts { get; init; }
        public bool PaymentTermsHitClientTimeout { get; init; }

        public List<Dictionary<string, object?>> OpenItems { get; init; } =
        [
            new() { ["PostingDate"] = "2026-05-06", ["DueDate"] = "2026-06-05", ["BalanceDueDebit"] = 400m, ["BalanceDueCredit"] = 0m }
        ];

        public ConcurrentBag<TimeSpan> Budgets { get; } = [];

        public int Attempts(string queryCode) => _attempts.GetValueOrDefault(queryCode);

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetPaymentTermsByCodeAsync) => PaymentTermsHitClientTimeout
                ? Task.FromException<PaymentTermsDto?>(new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 300 seconds elapsing.",
                    new TimeoutException()))
                : Task.FromResult<PaymentTermsDto?>(new PaymentTermsDto { GroupNumber = 3, NumberOfAdditionalDays = 30 }),
            nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryWithBudgetAsync) =>
                Answer((string)args![0]!, (TimeSpan)args[4]!),
            _ => throw new InvalidOperationException($"ISAPServiceLayerClient.{method.Name} was not expected.")
        });

        private Task<List<Dictionary<string, object?>>> Answer(string queryCode, TimeSpan budget)
        {
            Budgets.Add(budget);
            var attempt = _attempts.AddOrUpdate(queryCode, 1, (_, seen) => seen + 1);

            var timeouts = queryCode switch
            {
                GetCustomerStatementHandler.LedgerQueryCode => LedgerTimeouts,
                GetCustomerStatementHandler.OpeningBalanceQueryCode => OpeningTimeouts,
                GetCustomerStatementHandler.OpenItemsQueryCode => OpenItemsTimeouts,
                _ => 0
            };

            if (attempt <= timeouts)
            {
                return Task.FromException<List<Dictionary<string, object?>>>(
                    new TimeoutException($"SAP did not answer SQL query '{queryCode}' within {budget.TotalSeconds:0} seconds."));
            }

            return Task.FromResult(queryCode switch
            {
                GetCustomerStatementHandler.OpeningBalanceQueryCode =>
                [
                    new Dictionary<string, object?> { ["TotalDebit"] = 3000m, ["TotalCredit"] = 2000m }
                ],
                GetCustomerStatementHandler.LedgerQueryCode =>
                [
                    new Dictionary<string, object?>
                    {
                        ["PostingDate"] = "2026-05-06",
                        ["TransactionNumber"] = 501,
                        ["TransType"] = 13,
                        ["OriginNumber"] = "84565",
                        ["Debit"] = 400m,
                        ["Credit"] = 0m,
                        ["LineId"] = 0
                    }
                ],
                GetCustomerStatementHandler.OpenItemsQueryCode => OpenItems,
                _ => new List<Dictionary<string, object?>>()
            });
        }
    }
}
