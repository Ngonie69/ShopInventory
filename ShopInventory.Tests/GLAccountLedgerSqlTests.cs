using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.GLAccounts.Queries.GetGLAccountLedger;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Holds the G/L account ledger to two fixed SAP query objects, and to the arithmetic the
/// reconciliation line depends on.
/// </summary>
/// <remarks>
/// The leak this guards against is the one the customer statement shipped with: SQL text carrying
/// the account code and the date range creates a permanent OUQR row per request that nothing ever
/// reuses. Nothing about it is visible from the output — the numbers are right either way — so only
/// a test says whether it is happening. OUQR is already carrying thousands of leaked rows, and its
/// size is what makes creating the next query slow.
/// </remarks>
public class GLAccountLedgerSqlTests
{
    [Fact]
    public async Task Ledger_sql_carries_no_interpolated_values()
    {
        var calls = await RunLedgerAsync("510000");

        Assert.NotEmpty(calls);
        foreach (var call in calls)
        {
            Assert.DoesNotContain("510000", call.SqlText, StringComparison.Ordinal);
            Assert.DoesNotContain("2026-", call.SqlText, StringComparison.Ordinal);
            Assert.Contains(":acctCode", call.SqlText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Ledger_sql_is_identical_across_accounts_and_date_ranges()
    {
        var first = await RunLedgerAsync("510000", new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));
        var second = await RunLedgerAsync("620400", new DateTime(2025, 7, 15), new DateTime(2026, 2, 2));

        Assert.Equal(
            first.Select(call => (call.QueryCode, call.SqlText)).Distinct(),
            second.Select(call => (call.QueryCode, call.SqlText)).Distinct());

        // Named rather than counted, so growing a third query object is a deliberate edit here.
        Assert.Equal(
            ["GL_LEDGER_BALANCE", "GL_LEDGER_ROWS"],
            first.Select(call => call.QueryCode).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The opening balance and the reconciliation balance are the same statement run against two
    /// dates, which is the whole reason the reconciliation costs one round trip and no second SAP
    /// object.
    /// </summary>
    [Fact]
    public async Task Both_balance_reads_share_one_query_object()
    {
        var calls = await RunLedgerAsync("510000", new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));

        var balanceCalls = calls.Where(call => call.QueryCode == "GL_LEDGER_BALANCE").ToList();

        Assert.Equal(2, balanceCalls.Count);
        Assert.Single(balanceCalls.Select(call => call.SqlText).Distinct());
        Assert.Equal("2026-08-01", balanceCalls[0].Parameters["beforeDate"]);
        // Tomorrow, so "before" covers everything posted up to and including today — the figure
        // SAP's own account balance is comparable with.
        Assert.Equal(
            DateTime.UtcNow.Date.AddDays(1).ToString("yyyy-MM-dd"),
            balanceCalls[1].Parameters["beforeDate"]);
    }

    [Fact]
    public async Task Ledger_is_keyed_on_the_account_not_the_business_partner()
    {
        // JDT1."ShortName" is the customer statement's key and holds the partner code. Keying the
        // G/L ledger on it would return one customer's postings under an account number.
        var calls = await RunLedgerAsync("510000");

        var ledger = calls.Single(call => call.QueryCode == "GL_LEDGER_ROWS");
        Assert.Contains("T1.\"Account\" = :acctCode", ledger.SqlText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ShortName\" =", ledger.SqlText, StringComparison.Ordinal);
        Assert.Equal("510000", ledger.Parameters["acctCode"]);
    }

    [Fact]
    public async Task Dates_are_bound_as_yyyy_MM_dd()
    {
        // yyyyMMdd is accepted when the query object is created and then silently matches nothing.
        var calls = await RunLedgerAsync("510000", new DateTime(2026, 1, 9), new DateTime(2026, 3, 4));

        var ledger = calls.Single(call => call.QueryCode == "GL_LEDGER_ROWS");
        Assert.Equal("2026-01-09", ledger.Parameters["fromDate"]);
        Assert.Equal("2026-03-04", ledger.Parameters["toDate"]);
    }

    /// <summary>
    /// The SQLQueries validator rejects these outright, and it does so when the query object is
    /// created — so one of them takes down the whole page rather than one column.
    /// </summary>
    [Fact]
    public void Ledger_sql_uses_no_construct_the_sap_validator_rejects()
    {
        foreach (var sql in new[] { GetGLAccountLedgerHandler.BalanceSql, GetGLAccountLedgerHandler.LedgerSql })
        {
            Assert.DoesNotContain("CASE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("COALESCE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TO_DATE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("||", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("YEAR(", sql, StringComparison.OrdinalIgnoreCase);
            // A terminator is rejected as "Incorrect syntax near ';'".
            Assert.DoesNotContain(";", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Running_balance_starts_at_the_opening_balance_and_carries_forward()
    {
        var response = await RunLedgerForResponseAsync(
            openingDebit: 1_000m,
            openingCredit: 250m,
            ledgerRows:
            [
                Row(postingDate: "20260805", transactionNumber: 1, debit: 500m, credit: 0m),
                Row(postingDate: "20260806", transactionNumber: 2, debit: 0m, credit: 200m)
            ]);

        Assert.Equal(750m, response.OpeningBalance);
        Assert.Equal([1_250m, 1_050m], response.Lines.Select(line => line.Balance));
        Assert.Equal(1_050m, response.ClosingBalance);
        Assert.Equal(500m, response.TotalDebits);
        Assert.Equal(200m, response.TotalCredits);
    }

    /// <summary>
    /// SAP returns dates from SQLQueries as yyyyMMdd, which general parsing rejects — that left
    /// every line of the customer statement dated 01/01/0001.
    /// </summary>
    [Fact]
    public async Task Sap_compact_dates_are_read_as_real_dates()
    {
        var response = await RunLedgerForResponseAsync(
            ledgerRows: [Row(postingDate: "20200821", transactionNumber: 190707, debit: 926_198m, credit: 0m)]);

        var line = Assert.Single(response.Lines);
        Assert.Equal(new DateTime(2020, 8, 21), line.Date);
    }

    [Fact]
    public async Task Reconciliation_reports_the_gap_between_sap_and_the_journal()
    {
        // The shape of the known statement defect: every line is right, the total is 1.17 light.
        var response = await RunLedgerForResponseAsync(
            openingDebit: 1_000m,
            openingCredit: 0m,
            sapBalance: 1_001.17m);

        Assert.True(response.IsReconciled);
        Assert.Equal(1_000m, response.ComputedBalanceToday);
        Assert.Equal(1_001.17m, response.SapBalance);
        Assert.Equal(1.17m, response.ReconciliationDifference);
    }

    [Fact]
    public async Task A_failed_account_read_leaves_the_ledger_standing_without_its_reconciliation()
    {
        // A difference of zero would be a claim the numbers agree. They were never compared.
        var response = await RunLedgerForResponseAsync(
            openingDebit: 1_000m,
            accountReadThrows: true);

        Assert.False(response.IsReconciled);
        Assert.Equal(0m, response.ReconciliationDifference);
        Assert.Equal(1_000m, response.OpeningBalance);
    }

    [Fact]
    public async Task A_period_longer_than_the_cap_is_cut_and_says_so()
    {
        var rows = Enumerable
            .Range(1, GetGLAccountLedgerHandler.LineLimit + 1)
            .Select(number => Row("20260805", number, debit: 1m, credit: 0m))
            .ToList();

        var response = await RunLedgerForResponseAsync(ledgerRows: rows);

        Assert.True(response.IsTruncated);
        Assert.Equal(GetGLAccountLedgerHandler.LineLimit, response.Lines.Count);
    }

    [Fact]
    public async Task A_period_that_exactly_fills_the_cap_is_not_reported_as_cut()
    {
        // The reason the read asks for one row more than it shows: at exactly the limit there is
        // nothing missing, and a banner saying otherwise would send people hunting for lines that
        // do not exist.
        var rows = Enumerable
            .Range(1, GetGLAccountLedgerHandler.LineLimit)
            .Select(number => Row("20260805", number, debit: 1m, credit: 0m))
            .ToList();

        var response = await RunLedgerForResponseAsync(ledgerRows: rows);

        Assert.False(response.IsTruncated);
        Assert.Equal(GetGLAccountLedgerHandler.LineLimit, response.Lines.Count);
    }

    [Fact]
    public async Task The_read_is_capped_one_row_above_what_the_page_shows()
    {
        var calls = await RunLedgerAsync("510000");

        var ledger = calls.Single(call => call.QueryCode == "GL_LEDGER_ROWS");
        Assert.Equal(GetGLAccountLedgerHandler.LineLimit + 1, ledger.MaxRows);
    }

    [Fact]
    public async Task A_backwards_date_range_is_refused_before_sap_is_touched()
    {
        var calls = new List<SqlCall>();
        var result = await ExecuteAsync(
            calls,
            fromDate: new DateTime(2026, 6, 30),
            toDate: new DateTime(2026, 1, 1));

        Assert.True(result.IsError);
        Assert.Empty(calls);
    }

    private static Dictionary<string, object?> Row(
        string postingDate,
        int transactionNumber,
        decimal debit,
        decimal credit) =>
        new()
        {
            ["PostingDate"] = postingDate,
            ["TransactionNumber"] = transactionNumber,
            ["TransType"] = "30",
            ["LineId"] = 0,
            ["Debit"] = debit,
            ["Credit"] = credit
        };

    private sealed record SqlCall(
        string QueryCode,
        string SqlText,
        IReadOnlyDictionary<string, string> Parameters,
        int? MaxRows);

    private static async Task<List<SqlCall>> RunLedgerAsync(
        string accountCode,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var calls = new List<SqlCall>();
        await ExecuteAsync(calls, accountCode: accountCode, fromDate: fromDate, toDate: toDate);
        return calls;
    }

    private static async Task<GLAccountLedgerResponseDto> RunLedgerForResponseAsync(
        decimal openingDebit = 0m,
        decimal openingCredit = 0m,
        decimal sapBalance = 0m,
        List<Dictionary<string, object?>>? ledgerRows = null,
        bool accountReadThrows = false)
    {
        var result = await ExecuteAsync(
            new List<SqlCall>(),
            openingDebit: openingDebit,
            openingCredit: openingCredit,
            sapBalance: sapBalance,
            ledgerRows: ledgerRows,
            accountReadThrows: accountReadThrows);

        Assert.False(
            result.IsError,
            result.IsError ? string.Join("; ", result.Errors.Select(error => error.Description)) : string.Empty);
        return result.Value;
    }

    private static async Task<ErrorOr<GLAccountLedgerResponseDto>> ExecuteAsync(
        List<SqlCall> calls,
        string accountCode = "510000",
        DateTime? fromDate = null,
        DateTime? toDate = null,
        decimal openingDebit = 0m,
        decimal openingCredit = 0m,
        decimal sapBalance = 0m,
        List<Dictionary<string, object?>>? ledgerRows = null,
        bool accountReadThrows = false)
    {
        // A holder rather than a local: a ref local cannot be captured by the stub's lambda.
        var balanceCallCount = new int[1];

        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryAsync) =>
                RecordAndAnswer(calls, ledgerRows ?? [], openingDebit, openingCredit, balanceCallCount, args!),
            nameof(ISAPServiceLayerClient.GetGLAccountByCodeAsync) => accountReadThrows
                ? throw new InvalidOperationException("SAP is unreachable")
                : Task.FromResult<GLAccountDto?>(new GLAccountDto
                {
                    Code = accountCode,
                    Name = "Test Account",
                    AccountType = "at_Expenses",
                    Currency = "USD",
                    Balance = sapBalance,
                    IsActive = true
                }),
            _ => throw new InvalidOperationException($"unexpected call {method.Name}")
        });

        var handler = new GetGLAccountLedgerHandler(
            sap,
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<GetGLAccountLedgerHandler>.Instance);

        return await handler.Handle(
            new GetGLAccountLedgerQuery(
                accountCode,
                fromDate ?? new DateTime(2026, 1, 1),
                toDate ?? new DateTime(2026, 6, 30)),
            CancellationToken.None);
    }

    private static object RecordAndAnswer(
        List<SqlCall> calls,
        List<Dictionary<string, object?>> ledgerRows,
        decimal openingDebit,
        decimal openingCredit,
        int[] balanceCallCount,
        object?[] args)
    {
        var queryCode = (string)args[0]!;
        calls.Add(new SqlCall(
            queryCode,
            (string)args[2]!,
            (IReadOnlyDictionary<string, string>)args[3]!,
            args.Length > 5 ? (int?)args[5] : null));

        if (queryCode == "GL_LEDGER_ROWS")
        {
            return Task.FromResult(ledgerRows);
        }

        // Both balance reads answer from the same figures: the ledger rows the stub returns are the
        // whole of this account's history, so the balance before the period and the balance as at
        // today only differ by what the caller asked for.
        balanceCallCount[0]++;
        var periodMovement = balanceCallCount[0] == 1
            ? 0m
            : ledgerRows.Sum(row => (decimal)(row["Debit"] ?? 0m) - (decimal)(row["Credit"] ?? 0m));

        return Task.FromResult<List<Dictionary<string, object?>>>(
        [
            new Dictionary<string, object?>
            {
                ["TotalDebit"] = openingDebit + (periodMovement > 0 ? periodMovement : 0m),
                ["TotalCredit"] = openingCredit + (periodMovement < 0 ? -periodMovement : 0m)
            }
        ]);
    }
}
