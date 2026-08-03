using System.Globalization;
using ShopInventory.Features.Statements.Queries.GetCustomerStatement;
using Xunit.Abstractions;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// Pins the two properties the statement SQL has to have against a real Service Layer: SAP accepts
/// the parameterised statements, and running them does not grow OUQR.
/// </summary>
/// <remarks>
/// Unit tests cannot reach either. That SAP binds <c>:name</c> parameters at all, that it rejects
/// <c>TO_DATE</c>, that <c>IN (:codes)</c> silently matches nothing — none of it is visible without
/// asking the real thing. And the leak this guards against is invisible by construction: the old
/// interpolated SQL worked perfectly, it just left a row behind every time.
///
/// These tests reuse the same two fixed query codes the handler uses, so running them repeatedly
/// costs no SAP objects.
/// </remarks>
[Collection("SAP")]
public class SapStatementQueryTests(SapClientFixture fixture, ITestOutputHelper output)
{
    // The handler's own constants, not a copy of them. A copy would let the shipped SQL drift away
    // from the SQL this proves SAP accepts, which is the one thing these tests exist to establish.
    private const string OpeningBalanceQueryCode = GetCustomerStatementHandler.OpeningBalanceQueryCode;
    private const string LedgerQueryCode = GetCustomerStatementHandler.LedgerQueryCode;
    private const string OpeningBalanceSql = GetCustomerStatementHandler.OpeningBalanceSql;
    private const string LedgerSql = GetCustomerStatementHandler.LedgerSql;

    [SapSqlFact]
    public async Task Statement_sql_is_accepted_with_bound_parameters()
    {
        var cardCode = await FindCustomerWithLedgerLinesAsync();
        output.WriteLine($"card code: {cardCode}");

        var opening = await fixture.Client.ExecuteParameterisedSqlQueryAsync(
            OpeningBalanceQueryCode,
            "Statement Opening Balance",
            OpeningBalanceSql,
            new Dictionary<string, string> { ["cardCode"] = cardCode, ["fromDate"] = "2020-01-01" });

        Assert.Single(opening);
        output.WriteLine($"opening balance row: {string.Join(", ", opening[0].Select(p => $"{p.Key}={p.Value}"))}");

        var ledger = await fixture.Client.ExecuteParameterisedSqlQueryAsync(
            LedgerQueryCode,
            "Statement Ledger Rows",
            LedgerSql,
            new Dictionary<string, string>
            {
                ["cardCode"] = cardCode,
                ["fromDate"] = "2019-01-01",
                ["toDate"] = "2021-01-01"
            });

        Assert.NotEmpty(ledger);
        output.WriteLine($"ledger rows: {ledger.Count}");
        output.WriteLine($"first row: {string.Join(", ", ledger[0].Select(p => $"{p.Key}={p.Value}"))}");

        // Every column the handler binds must actually come back, or the statement silently shows
        // blanks; and the date must be the yyyyMMdd the handler now parses.
        foreach (var column in new[]
        {
            "PostingDate", "TransactionNumber", "TransType", "OriginNumber", "JournalMemo",
            "CreatedBy", "LineId", "OffsetAccount", "Details", "Debit", "Credit", "Currency"
        })
        {
            Assert.True(ledger[0].ContainsKey(column), $"SAP did not return column '{column}'");
        }

        var postingDate = ledger[0]["PostingDate"]?.ToString();
        Assert.True(
            DateTime.TryParseExact(postingDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            $"PostingDate '{postingDate}' is not the yyyyMMdd the handler parses.");
    }

    /// <summary>
    /// The point of the whole change: a statement run must not add SAP query objects, however many
    /// different customers and date ranges it is asked for.
    /// </summary>
    [SapSqlFact]
    public async Task Repeated_statement_runs_add_no_sap_query_objects()
    {
        var cardCode = await FindCustomerWithLedgerLinesAsync();

        // Provision first, so the baseline is taken with both objects already in place.
        await RunStatementPairAsync(cardCode, "2020-01-01", "2021-01-01");

        var before = await CountSqlQueriesAsync();
        output.WriteLine($"SQLQueries before: {before}");

        for (var i = 1; i <= 4; i++)
        {
            await RunStatementPairAsync(cardCode, $"20{20 + i:D2}-01-0{1 + i}", "2026-01-01");
        }

        var after = await CountSqlQueriesAsync();
        output.WriteLine($"SQLQueries after 4 further statement runs: {after}");

        Assert.Equal(before, after);
    }

    /// <summary>
    /// Card codes reach this query from the request body, so a quote in one must stay data.
    /// </summary>
    [SapSqlFact]
    public async Task A_quote_in_a_card_code_stays_inside_the_bound_value()
    {
        var real = await FindCustomerWithLedgerLinesAsync();

        var baseline = await LedgerCountAsync(real);
        Assert.True(baseline > 0, "picked a customer with no rows, so the comparison proves nothing");

        // Both are ordinary values that match no customer. If either escaped its literal, the
        // tautology would widen the result instead of emptying it.
        foreach (var hostile in new[] { "O'Brien", $"{real}' OR '1'='1", "x'; DROP TABLE OJDT --" })
        {
            var rows = await LedgerCountAsync(hostile);
            output.WriteLine($"{hostile} -> {rows} rows");
            Assert.Equal(0, rows);
        }

        // And the real code still works afterwards, so nothing was damaged along the way.
        Assert.Equal(baseline, await LedgerCountAsync(real));
    }

    private async Task<int> LedgerCountAsync(string cardCode)
    {
        var rows = await fixture.Client.ExecuteParameterisedSqlQueryAsync(
            LedgerQueryCode, "Statement Ledger Rows", LedgerSql,
            new Dictionary<string, string>
            {
                ["cardCode"] = cardCode,
                ["fromDate"] = "2019-01-01",
                ["toDate"] = "2021-01-01"
            });

        return rows.Count;
    }

    private async Task RunStatementPairAsync(string cardCode, string fromDate, string toDate)
    {
        await fixture.Client.ExecuteParameterisedSqlQueryAsync(
            OpeningBalanceQueryCode, "Statement Opening Balance", OpeningBalanceSql,
            new Dictionary<string, string> { ["cardCode"] = cardCode, ["fromDate"] = fromDate });

        await fixture.Client.ExecuteParameterisedSqlQueryAsync(
            LedgerQueryCode, "Statement Ledger Rows", LedgerSql,
            new Dictionary<string, string>
            {
                ["cardCode"] = cardCode,
                ["fromDate"] = fromDate,
                ["toDate"] = toDate
            });
    }

    /// <summary>
    /// OUQR itself is not readable through SQLQueries ("Table 'OUQR' not accessible"), so count the
    /// entity set. This runs no SQL and so cannot perturb what it measures.
    /// </summary>
    private async Task<int> CountSqlQueriesAsync()
    {
        var codes = await fixture.Client.GetSqlQueryCodesAsync();
        return codes.Count;
    }

    /// <summary>
    /// Picks a customer that actually has journal lines. A customer without any would let both
    /// tests pass on empty results and prove nothing.
    /// </summary>
    private async Task<string> FindCustomerWithLedgerLinesAsync()
    {
        // Fixed text, so this sample costs one reusable SAP query object shared by both tests.
        var rows = await fixture.Client.ExecuteRawSqlQueryAsync(
            "STMT_TEST_SAMPLE",
            "Statement test customer sample",
            """
SELECT TOP 400 T1."ShortName" AS "CardCode"
FROM JDT1 T1
INNER JOIN OCRD T2 ON T2."CardCode" = T1."ShortName"
WHERE T2."CardType" = 'C'
""");

        var cardCode = rows
            .Select(row => row.TryGetValue("CardCode", out var v) ? v?.ToString() : null)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .GroupBy(code => code!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();

        Assert.False(
            string.IsNullOrWhiteSpace(cardCode),
            "No customer with journal lines found, so the statement queries cannot be exercised.");

        return cardCode!;
    }
}
