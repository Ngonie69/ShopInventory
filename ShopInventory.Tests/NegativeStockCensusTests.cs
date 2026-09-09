using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.Reports.Queries.GetNegativeStockTrend;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The outcome measure: how much stock SAP is actually holding below zero, over time.
/// </summary>
/// <remarks>
/// Every other guard built against negative stock stops a document that <i>would</i> take stock
/// under, and not one of them can say whether it worked. This is the number that can, and only by
/// being read repeatedly — one day's figure says nothing, a fortnight of them says everything.
/// </remarks>
public sealed class NegativeStockCensusTests
{
    // ---------------------------------------------------------------
    // One measurement, two callers
    // ---------------------------------------------------------------

    [Fact]
    public void The_job_and_the_report_script_ask_SAP_the_same_question()
    {
        // Not "a similar question". The statement the client sends and the one the script sends are
        // the same characters, so they derive the same content-addressed code, resolve to the same
        // SAP object, and cannot drift into measuring slightly different things. The baseline a
        // person takes by hand is therefore comparable with the series the job records — which is
        // the entire point of having both.
        var script = ReadScriptStatement("SQL_WAREHOUSE_NEGATIVES");

        Assert.Equal(
            SAPServiceLayerClient.NormalizeSqlText(script),
            SAPServiceLayerClient.NormalizeSqlText(SAPServiceLayerClient.NegativeStockSql));

        Assert.Equal(
            "NEGSTK_WHS_9383E60D10BB",
            SAPServiceLayerClient.BuildContentAddressedQueryCode("NEGSTK_WHS", SAPServiceLayerClient.NegativeStockSql));
    }

    [Fact]
    public void The_census_statement_stays_a_constant()
    {
        // A SQLQueries object cannot practically be deleted, so a statement carrying interpolated
        // values leaks a permanent row per distinct value. This one asks a whole-company question
        // and needs no values at all.
        Assert.DoesNotContain("{", SAPServiceLayerClient.NegativeStockSql);
        Assert.DoesNotContain(";", SAPServiceLayerClient.NegativeStockSql);
        Assert.Contains("< 0", SAPServiceLayerClient.NegativeStockSql);
    }

    // ---------------------------------------------------------------
    // The trend
    // ---------------------------------------------------------------

    [Fact]
    public async Task The_trend_reads_oldest_first_so_it_can_be_seen_falling()
    {
        await using var context = EmptyContext();
        await Observe(context, DaysAgo(3), ("KEFSHOP", "CHE011", -10), ("KEFSHOP", "BON001", -5));
        await Observe(context, DaysAgo(2), ("KEFSHOP", "CHE011", -4));
        await Observe(context, DaysAgo(1), ("KEFSHOP", "CHE011", -1));

        var trend = (await Handle(context, days: 30)).Value;

        Assert.Equal([15m, 4m, 1m], trend.History.Select(day => day.UnitsBelowZero));
        Assert.Equal(1m, trend.Latest!.UnitsBelowZero);
    }

    [Fact]
    public async Task Units_are_reported_as_a_positive_figure()
    {
        await using var context = EmptyContext();
        await Observe(context, DaysAgo(1), ("KEFSHOP", "CHE011", -12.5m));

        // "412 units below zero" is a number people compare. "-412" invites an argument about which
        // direction is the good one.
        Assert.Equal(12.5m, (await Handle(context, 30)).Value.Latest!.UnitsBelowZero);
    }

    [Fact]
    public async Task Rows_and_units_answer_different_questions()
    {
        await using var context = EmptyContext();
        await Observe(context, DaysAgo(1),
            ("KEFSHOP", "CHE011", -1), ("KEFSHOP", "BON001", -1), ("VAN004", "CHE011", -200));

        var latest = (await Handle(context, 30)).Value.Latest!;

        // Three items are wrong, in two warehouses, but almost all the damage is one of them.
        Assert.Equal(3, latest.Rows);
        Assert.Equal(2, latest.Warehouses);
        Assert.Equal(202m, latest.UnitsBelowZero);
    }

    [Fact]
    public async Task The_worst_warehouses_say_where_to_look_first()
    {
        await using var context = EmptyContext();
        await Observe(context, DaysAgo(1),
            ("KEFSHOP", "CHE011", -2), ("VAN004", "CHE011", -90), ("VAN004", "BON001", -10));

        var worst = (await Handle(context, 30)).Value.WorstWarehouses;

        Assert.Equal("VAN004", worst[0].WarehouseCode);
        Assert.Equal(100m, worst[0].UnitsBelowZero);
        Assert.Equal(2, worst[0].Rows);
    }

    [Fact]
    public async Task Days_outside_the_window_are_left_out()
    {
        await using var context = EmptyContext();
        await Observe(context, DaysAgo(40), ("KEFSHOP", "CHE011", -99));
        await Observe(context, DaysAgo(1), ("KEFSHOP", "CHE011", -1));

        var trend = (await Handle(context, days: 7)).Value;

        Assert.Single(trend.History);
        Assert.Equal(1m, trend.Latest!.UnitsBelowZero);
    }

    [Fact]
    public async Task A_company_with_nothing_negative_reads_as_nothing_not_as_an_error()
    {
        await using var context = EmptyContext();

        var trend = (await Handle(context, 30)).Value;

        // The state the whole effort is aiming at, and it has to be distinguishable from "never
        // counted". Latest is null when no census has run; a day with no negatives records no rows,
        // so it is absent from history rather than present as a zero.
        Assert.Null(trend.Latest);
        Assert.Empty(trend.History);
        Assert.Empty(trend.WorstWarehouses);
    }

    [Fact]
    public async Task An_absurd_window_is_clamped_rather_than_refused()
    {
        await using var context = EmptyContext();

        Assert.Equal(365, (await Handle(context, days: 100_000)).Value.Days);
        Assert.Equal(1, (await Handle(context, days: 0)).Value.Days);
        Assert.Equal(1, (await Handle(context, days: -5)).Value.Days);
    }

    // ---------------------------------------------------------------

    private static Task<ErrorOr.ErrorOr<NegativeStockTrendDto>> Handle(ApplicationDbContext context, int days) =>
        new GetNegativeStockTrendHandler(context).Handle(new GetNegativeStockTrendQuery(days), CancellationToken.None);

    private static DateTime DaysAgo(int days) => DateTime.UtcNow.Date.AddDays(-days);

    private static async Task Observe(
        ApplicationDbContext context,
        DateTime day,
        params (string Warehouse, string Item, decimal OnHand)[] rows)
    {
        foreach (var row in rows)
        {
            context.NegativeStockObservations.Add(new NegativeStockObservationEntity
            {
                ObservedOn = day,
                WarehouseCode = row.Warehouse,
                ItemCode = row.Item,
                OnHand = row.OnHand
            });
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Named rather than written as a character literal, which does not survive being edited
    /// through a shell — the escape level is eaten and the file stops compiling in a way that looks
    /// like a typo rather than a quoting problem.
    /// </summary>
    private const char Backslash = (char)92;

    /// <summary>
    /// Reads a triple-quoted statement out of the Python report script.
    /// </summary>
    private static string ReadScriptStatement(string name)
    {
        var script = Path.Combine(RepositoryRoot(), "scripts", "NegativeStock", "report_negative_stock.py");
        var text = File.ReadAllText(script);

        var start = text.IndexOf($"{name} = \"\"\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} not found in {script}");

        start += $"{name} = \"\"\"".Length;

        // The closing delimiter, not the escaped quote that precedes it. These statements end on a
        // quoted SQL identifier, so the source reads ...ItemCode\"""" — four quotes, of which the
        // first belongs to the string and the last three close it. Taking the first run of three
        // would cut the statement one character short and the comparison would fail for a reason
        // that has nothing to do with the statements differing.
        var end = start;
        while (true)
        {
            end = text.IndexOf("\"\"\"", end, StringComparison.Ordinal);
            Assert.True(end > start, $"{name} is not terminated in {script}");

            if (text[end - 1] != Backslash)
            {
                break;
            }

            end++;
        }

        // Python escapes that quote; C# raw strings do not.
        return text[start..end].Replace("\\\"", "\"");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static ApplicationDbContext EmptyContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);
        context.Database.EnsureCreated();
        return context;
    }
}
