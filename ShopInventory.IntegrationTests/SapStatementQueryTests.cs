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
///
/// Every ledger read is bounded to a short window around a date the chosen customer is known to
/// have rows on — see <see cref="FindPopulatedStatementWindowAsync"/>. Two things were measured to
/// arrive at that, both counter-intuitive enough to be worth writing down:
///
/// Selecting the *busiest* customer and reading its whole history is what a "make sure it isn't
/// empty" helper naturally does, and it cost over two minutes — for the parameterised and literal
/// forms alike, so it says nothing about this change.
///
/// A single-day window is not the cheap answer. <c>RefDate &gt;= X AND RefDate &lt;= X</c> is a
/// degenerate range and timed out on every attempt, while the surrounding month came back in 2.5s
/// and the surrounding year in 0.3s. Hence a window with width rather than a single date.
///
/// The SAP instance behind these tests is also genuinely variable — the same request has been seen
/// at 0.1s and at over 25s — so treat an isolated timeout here as the environment, not a
/// regression, and re-run before believing it.
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

    [SapFact]
    public async Task Statement_sql_is_accepted_with_bound_parameters()
    {
        var (cardCode, from, to) = await FindPopulatedStatementWindowAsync();
        output.WriteLine($"card code: {cardCode}, window: {from} .. {to}");

        var opening = await fixture.Client.ExecuteParameterisedSqlQueryAsync(
            OpeningBalanceQueryCode,
            "Statement Opening Balance",
            OpeningBalanceSql,
            new Dictionary<string, string> { ["cardCode"] = cardCode, ["fromDate"] = from });

        Assert.Single(opening);
        output.WriteLine($"opening balance row: {string.Join(", ", opening[0].Select(p => $"{p.Key}={p.Value}"))}");

        var ledger = await LedgerRowsAsync(cardCode, from, to);

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
    [SapFact]
    public async Task Repeated_statement_runs_add_no_sap_query_objects()
    {
        var (cardCode, from, to) = await FindPopulatedStatementWindowAsync();

        // Provision first, so the baseline is taken with both objects already in place.
        await RunStatementPairAsync(cardCode, from, to);

        var before = await CountSqlQueriesAsync();
        output.WriteLine($"SQLQueries before: {before}");

        // Distinct parameter values each time — a different customer and a different window — which
        // under the old interpolated SQL was a new SAP query object per call. The card codes match
        // nothing, which is the cheap way to vary the parameters without varying the cost.
        var start = DateTime.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        for (var i = 1; i <= 4; i++)
        {
            var shifted = start.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await RunStatementPairAsync($"{cardCode}{new string('X', i)}", shifted, to);
        }

        var after = await CountSqlQueriesAsync();
        output.WriteLine($"SQLQueries after 4 further statement runs: {after}");

        Assert.Equal(before, after);
    }

    /// <summary>
    /// Card codes reach this query from the request body, so a quote in one must stay data.
    /// </summary>
    [SapFact]
    public async Task A_quote_in_a_card_code_stays_inside_the_bound_value()
    {
        var (real, from, to) = await FindPopulatedStatementWindowAsync();

        var baseline = (await LedgerRowsAsync(real, from, to)).Count;
        Assert.True(baseline > 0, "picked a customer with no rows, so the comparison proves nothing");

        // Both are ordinary values that match no customer. If either escaped its literal, the
        // tautology would widen the result instead of emptying it.
        foreach (var hostile in new[] { "O'Brien", $"{real}' OR '1'='1", "x'; DROP TABLE OJDT --" })
        {
            var rows = (await LedgerRowsAsync(hostile, from, to)).Count;
            output.WriteLine($"{hostile} -> {rows} rows");
            Assert.Equal(0, rows);
        }

        // And the real code still works afterwards, so nothing was damaged along the way.
        Assert.Equal(baseline, (await LedgerRowsAsync(real, from, to)).Count);
    }

    private async Task<List<Dictionary<string, object?>>> LedgerRowsAsync(string cardCode, string from, string to) =>
        await fixture.Client.ExecuteParameterisedSqlQueryAsync(
            LedgerQueryCode, "Statement Ledger Rows", LedgerSql,
            new Dictionary<string, string>
            {
                ["cardCode"] = cardCode,
                ["fromDate"] = from,
                ["toDate"] = to
            });

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
    /// Picks a customer and a short date window that customer provably has rows in, so every ledger
    /// read below is both cheap and guaranteed non-empty.
    /// </summary>
    /// <remarks>
    /// Sampling the customer and the date together is the point. Picking a customer and then
    /// guessing at a window gives you either an empty result — which lets a test pass while proving
    /// nothing — or a multi-year scan. Taking the first sampled pair rather than the most frequent
    /// one also keeps this off the busiest account in the company.
    ///
    /// The date SAP returns here is yyyyMMdd, the same format the handler parses, so it is
    /// converted before being bound back as a parameter.
    /// </remarks>
    private async Task<(string CardCode, string From, string To)> FindPopulatedStatementWindowAsync()
    {
        // Fixed text, so this sample costs one reusable SAP query object shared by every test here.
        var rows = await fixture.Client.ExecuteRawSqlQueryAsync(
            "STMT_TEST_SAMPLE",
            "Statement test customer sample",
            // Table names quoted for the same reason the handler quotes them: SAP stores them that
            // way, and text that does not round-trip is PATCHed on every cold call.
            """
SELECT TOP 200 T1."ShortName" AS "CardCode", T0."RefDate" AS "PostingDate"
FROM "JDT1" T1
INNER JOIN "OJDT" T0 ON T0."TransId" = T1."TransId"
INNER JOIN "OCRD" T2 ON T2."CardCode" = T1."ShortName"
WHERE T2."CardType" = 'C'
""");

        var pair = rows
            .Select(row => (
                CardCode: row.TryGetValue("CardCode", out var c) ? c?.ToString() : null,
                PostingDate: row.TryGetValue("PostingDate", out var d) ? d?.ToString() : null))
            .FirstOrDefault(row =>
                !string.IsNullOrWhiteSpace(row.CardCode)
                && DateTime.TryParseExact(
                    row.PostingDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));

        Assert.False(
            string.IsNullOrWhiteSpace(pair.CardCode),
            "No customer journal line with a readable posting date was found, so the statement queries cannot be exercised.");

        // Width, not a single date: an equality range times out where a real range is fast.
        var day = DateTime.ParseExact(pair.PostingDate!, "yyyyMMdd", CultureInfo.InvariantCulture);

        return (
            pair.CardCode!,
            day.AddDays(-15).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            day.AddDays(15).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }
}
