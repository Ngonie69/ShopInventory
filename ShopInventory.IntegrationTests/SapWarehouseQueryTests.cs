using Xunit.Abstractions;

namespace ShopInventory.IntegrationTests;

/// <summary>
/// The warehouse sales report and the batch search, against a real Service Layer: SAP accepts both
/// statements, and running them over new warehouses, ranges and search terms does not grow OUQR.
/// </summary>
/// <remarks>
/// Both used to put request data in the SqlCode — <c>SALES_QTY_&lt;whs&gt;_&lt;from&gt;_&lt;to&gt;</c>
/// and a content-addressed hash of the user's search term — so each new range or term left a
/// permanent query object behind. That is invisible from inside the application: the rows returned
/// were correct, and only the SAP-side count moved.
///
/// Acceptance needs a real validator for the same reason the reports did, and both statements found
/// one of its limits. The sales statement ended <c>ORDER BY SUM(T1."Quantity") DESC</c>, which SAP
/// refuses at create, so it can never have been provisioned — a failing feature that looked from the
/// code like a leaking one. The batch search folded case with <c>UPPER(T0."DistNumber")</c>, which
/// cannot be compared against a bound parameter at all; that shaped the statement it has now, and
/// <see cref="The_validator_still_rejects_the_constructs_the_batch_search_works_around"/> keeps the
/// reason on the record.
/// </remarks>
[Collection("SAP")]
public class SapWarehouseQueryTests(SapClientFixture fixture, ITestOutputHelper output)
{
    private static readonly (DateTime From, DateTime To)[] Ranges =
    [
        (new DateTime(2023, 1, 1), new DateTime(2023, 12, 31)),
        (new DateTime(2023, 7, 5), new DateTime(2023, 7, 6)),
        (new DateTime(2023, 8, 9), new DateTime(2023, 8, 11))
    ];

    /// <summary>
    /// Narrow on purpose. A term matching most of OBTN — a bare <c>0</c> does — makes SAP sort the
    /// whole match set and times the request out at 300s, which is a property of the search itself
    /// and not of anything these tests are checking.
    /// </summary>
    private const string MatchingSearchTerm = "A1";

    private static readonly string[] SearchTerms =
        [MatchingSearchTerm, "B24", "ZZ9", "ZZZ-NOTHING-MATCHES-THIS"];

    [SapSqlFact]
    public async Task The_warehouse_sales_statement_is_accepted()
    {
        var warehouse = await FirstWarehouseCodeAsync();
        var (from, to) = Ranges[0];

        var sales = await fixture.Client.GetSalesQuantitiesByWarehouseAsync(warehouse, from, to);

        output.WriteLine($"{warehouse} {from:yyyy-MM-dd}..{to:yyyy-MM-dd}: {sales.Count} items");

        // An empty result is not a failure — the test company need not have sold anything in the
        // range. Reaching this line at all means the create was accepted.
        foreach (var sale in sales.Take(5))
        {
            output.WriteLine($"  {sale.ItemCode} qty {sale.TotalQuantitySold} value {sale.TotalSalesValue}");
        }

        Assert.Equal(
            sales.Select(sale => sale.TotalQuantitySold).OrderByDescending(quantity => quantity),
            sales.Select(sale => sale.TotalQuantitySold));
    }

    [SapSqlFact]
    public async Task The_batch_search_statement_is_accepted()
    {
        var results = await fixture.Client.SearchBatchesByBatchNumberAsync(MatchingSearchTerm);

        output.WriteLine($"'{MatchingSearchTerm}': {results.Count} batches");

        foreach (var result in results.Take(5))
        {
            output.WriteLine($"  {result.BatchNumber} / {result.ItemCode} in {result.WarehouseCode}");
        }

        Assert.All(results, result => Assert.Contains(
            MatchingSearchTerm,
            result.BatchNumber,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The point of the change: neither path may add a query object, however many warehouses, date
    /// ranges and search terms it is asked for.
    /// </summary>
    [SapSqlFact]
    public async Task Further_ranges_and_search_terms_add_no_sap_query_objects()
    {
        var warehouse = await FirstWarehouseCodeAsync();

        // Provision both objects first, so the baseline is taken with them already in place.
        await fixture.Client.GetSalesQuantitiesByWarehouseAsync(warehouse, Ranges[0].From, Ranges[0].To);
        await fixture.Client.SearchBatchesByBatchNumberAsync(MatchingSearchTerm);

        var before = await CountSqlQueriesAsync();
        output.WriteLine($"SQLQueries before: {before}");

        foreach (var (from, to) in Ranges[1..])
        {
            await fixture.Client.GetSalesQuantitiesByWarehouseAsync(warehouse, from, to);
        }

        foreach (var term in SearchTerms[1..])
        {
            await fixture.Client.SearchBatchesByBatchNumberAsync(term);
        }

        var after = await CountSqlQueriesAsync();
        output.WriteLine(
            $"SQLQueries after {Ranges.Length - 1} further ranges and {SearchTerms.Length - 1} further terms: {after}");

        Assert.Equal(before, after);
    }

    /// <summary>
    /// Why the batch search matches the column bare against three cased forms of the term instead of
    /// folding the column's case the way it used to.
    /// </summary>
    /// <remarks>
    /// Safe to run repeatedly: the create is what fails, so nothing is left behind under this code.
    /// A failure here means SAP has started accepting the construct, and the search could go back to
    /// a single fully case-insensitive parameter — not that anything is broken.
    /// </remarks>
    [SapSqlTheory]
    [InlineData(
        "a parameter behind a function on the column",
        """SELECT T0."AbsEntry" AS "A" FROM OBTN T0 WHERE UPPER(T0."DistNumber") LIKE :searchTerm""")]
    [InlineData(
        "wildcards concatenated onto the parameter",
        """SELECT T0."AbsEntry" AS "A" FROM OBTN T0 WHERE T0."DistNumber" LIKE '%' || :searchTerm || '%'""")]
    public async Task The_validator_still_rejects_the_constructs_the_batch_search_works_around(
        string construct,
        string sql)
    {
        var rejection = await Assert.ThrowsAnyAsync<Exception>(() =>
            fixture.Client.ExecuteParameterisedSqlQueryAsync(
                "BATCH_REJECTED_PROBE",
                "Batch search validator probe",
                sql,
                new Dictionary<string, string> { ["searchTerm"] = "%A%" }));

        output.WriteLine($"{construct}: {rejection.Message}");
    }

    /// <summary>
    /// Any warehouse the company actually defines. Hard-coding one makes the test fail on a company
    /// that spells its warehouses differently, which reads as a broken statement and is not one.
    /// </summary>
    private async Task<string> FirstWarehouseCodeAsync()
    {
        var warehouses = await fixture.Client.GetWarehousesAsync();
        var code = warehouses
            .Select(warehouse => warehouse.WarehouseCode)
            .FirstOrDefault(warehouseCode => !string.IsNullOrWhiteSpace(warehouseCode));

        Assert.False(string.IsNullOrWhiteSpace(code), "The configured company defines no warehouses.");
        return code!;
    }

    private async Task<int> CountSqlQueriesAsync()
    {
        var codes = await fixture.Client.GetSqlQueryCodesAsync();
        return codes.Count;
    }
}
