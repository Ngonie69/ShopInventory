using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Features.Statements.Queries.GetCustomerStatement;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Holds the statement queries to a fixed set of SAP query objects.
/// </summary>
/// <remarks>
/// The statement SQL used to interpolate the card codes and the from/to dates straight into the
/// statement text and run it through the content-addressed executor. That executor only reuses a
/// SAP-side object when the text is byte-identical, and statement text never is: the date range
/// moves every day and every customer has a different code. So each statement view created two
/// permanent OUQR rows that were never reused and, at OUQR's size, could not practically be
/// deleted — the leak removed everywhere else, reintroduced by construction.
///
/// What makes it worth a test rather than a comment is that nothing about it is visible from
/// outside: the interpolated version returned exactly the right numbers. Only the SAP-side row
/// count moved.
/// </remarks>
public class CustomerStatementSqlTests
{
    [Fact]
    public async Task Statement_sql_carries_no_interpolated_values()
    {
        var calls = await RunStatementAsync(["ACME", "ACME2"]);

        Assert.NotEmpty(calls);
        foreach (var call in calls)
        {
            Assert.DoesNotContain("ACME", call.SqlText, StringComparison.Ordinal);
            Assert.DoesNotContain("2026-", call.SqlText, StringComparison.Ordinal);
            Assert.Contains(":cardCode", call.SqlText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Statement_sql_is_identical_across_customers_and_date_ranges()
    {
        var first = await RunStatementAsync(["ACME"], new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));
        var second = await RunStatementAsync(["OTHER"], new DateTime(2025, 7, 15), new DateTime(2026, 2, 2));

        // Same query codes and the same statements, so SAP holds one object per shape forever.
        Assert.Equal(
            first.Select(call => (call.QueryCode, call.SqlText)).Distinct(),
            second.Select(call => (call.QueryCode, call.SqlText)).Distinct());

        Assert.Equal(2, first.Select(call => call.QueryCode).Distinct().Count());
    }

    [Fact]
    public async Task Every_card_code_is_queried_because_a_parameter_cannot_carry_an_in_list()
    {
        // SAP binds a parameter as one literal: `IN (:codes)` with "A,B" matches nothing and says so
        // as zero rows, not an error. One execution per code is the only correct shape.
        var calls = await RunStatementAsync(["ACME", "ACME2", "ACME3"]);

        var ledgerCardCodes = calls
            .Where(call => call.QueryCode == "STMT_LEDGER_ROWS")
            .Select(call => call.Parameters["cardCode"])
            .ToList();

        // The requested codes in order, then the primary the statement was opened on.
        Assert.Equal(["ACME", "ACME2", "ACME3"], ledgerCardCodes);
    }

    [Fact]
    public async Task Dates_are_bound_as_yyyy_MM_dd()
    {
        var calls = await RunStatementAsync(["ACME"], new DateTime(2026, 1, 9), new DateTime(2026, 3, 4));

        var ledger = calls.Single(call => call.QueryCode == "STMT_LEDGER_ROWS");
        Assert.Equal("2026-01-09", ledger.Parameters["fromDate"]);
        Assert.Equal("2026-03-04", ledger.Parameters["toDate"]);
    }

    /// <summary>
    /// SAP returns dates from SQLQueries as yyyyMMdd. General parsing rejects that, which left every
    /// statement line dated 01/01/0001.
    /// </summary>
    [Fact]
    public async Task Sap_compact_dates_are_read_as_real_dates()
    {
        var response = await RunStatementForResponseAsync(
            ledgerRows:
            [
                new Dictionary<string, object?>
                {
                    ["PostingDate"] = "20200821",
                    ["TransactionNumber"] = 190707,
                    ["TransType"] = "13",
                    ["LineId"] = 0,
                    ["Debit"] = 926198.0m,
                    ["Credit"] = 0m
                }
            ]);

        var line = Assert.Single(response.Lines);
        Assert.Equal(new DateTime(2020, 8, 21), line.Date);
    }

    private sealed record SqlCall(string QueryCode, string SqlText, IReadOnlyDictionary<string, string> Parameters);

    private static async Task<List<SqlCall>> RunStatementAsync(
        string[] cardCodes,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var calls = new List<SqlCall>();
        await ExecuteAsync(calls, ledgerRows: [], cardCodes, fromDate, toDate);
        return calls;
    }

    private static async Task<CustomerStatementResponseDto> RunStatementForResponseAsync(
        List<Dictionary<string, object?>> ledgerRows)
    {
        var calls = new List<SqlCall>();
        return await ExecuteAsync(calls, ledgerRows, ["ACME"], null, null);
    }

    private static async Task<CustomerStatementResponseDto> ExecuteAsync(
        List<SqlCall> calls,
        List<Dictionary<string, object?>> ledgerRows,
        string[] cardCodes,
        DateTime? fromDate,
        DateTime? toDate)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryAsync) =>
                RecordAndAnswer(calls, ledgerRows, args!),
            nameof(ISAPServiceLayerClient.GetOpenInvoicesByCustomersAsync) =>
                Task.FromResult(new List<Invoice>()),
            nameof(ISAPServiceLayerClient.GetPaymentTermsByCodeAsync) =>
                Task.FromResult<PaymentTermsDto?>(null),
            _ => throw new InvalidOperationException($"unexpected call {method.Name}")
        });

        var partners = StubProxy.For<IBusinessPartnerService>((method, _) =>
            method.Name == nameof(IBusinessPartnerService.GetBusinessPartnerByCodeAsync)
                ? Task.FromResult<BusinessPartnerDto?>(new BusinessPartnerDto
                {
                    CardCode = cardCodes[^1],
                    CardName = "Test Customer"
                })
                : throw new InvalidOperationException($"unexpected call {method.Name}"));

        var handler = new GetCustomerStatementHandler(
            partners,
            sap,
            NullLogger<GetCustomerStatementHandler>.Instance);

        var result = await handler.Handle(
            new GetCustomerStatementQuery(
                CardCode: cardCodes[^1],
                FromDate: fromDate ?? new DateTime(2026, 1, 1),
                ToDate: toDate ?? new DateTime(2026, 6, 30),
                CardCodes: cardCodes[..^1]),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? string.Join("; ", result.Errors.Select(e => e.Description)) : string.Empty);
        return result.Value;
    }

    private static object RecordAndAnswer(
        List<SqlCall> calls,
        List<Dictionary<string, object?>> ledgerRows,
        object?[] args)
    {
        var queryCode = (string)args[0]!;
        calls.Add(new SqlCall(
            queryCode,
            (string)args[2]!,
            (IReadOnlyDictionary<string, string>)args[3]!));

        return Task.FromResult(queryCode == "STMT_LEDGER_ROWS"
            ? ledgerRows
            : new List<Dictionary<string, object?>>());
    }
}
