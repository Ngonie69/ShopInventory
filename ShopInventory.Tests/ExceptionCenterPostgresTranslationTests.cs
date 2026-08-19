using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Compiles each Exception Center source query against the real PostgreSQL provider.
/// </summary>
/// <remarks>
/// The rest of this suite runs on SQLite, chosen because it is a real relational provider and so fails a
/// query that cannot become SQL at all. It stores every DateTime as TEXT, though, so it cannot see the one
/// distinction these queries turn on. PostgreSQL has two timestamp types and refuses to apply a single
/// operation across them, and the fiscal rows carry both: a fiscal day's <c>OpenedAtLocal</c> and a van
/// sale's <c>ReceiptDate</c> are the taxpayer's wall clock, stored as 'timestamp without time zone',
/// while every other date on those rows is a UTC instant. Ordering a query on a coalesce of one of each
/// translated cleanly on SQLite, passed this suite, and threw
/// <see cref="NotSupportedException"/> on every single load of the production dashboard.
///
/// <para>
/// Nothing here needs a database. EF compiles a query to SQL before it opens a connection, so pointing the
/// context at an address with nothing behind it separates the two outcomes cleanly: a query that cannot be
/// translated fails while it is being compiled and never involves the driver at all, while a query that
/// translates gets as far as Npgsql trying to connect. Getting that far is the assertion.
/// </para>
/// </remarks>
public sealed class ExceptionCenterPostgresTranslationTests
{
    /// <summary>Nothing listens on port 1, and the one-second timeout bounds how long that takes to learn.</summary>
    private const string NowhereConnectionString =
        "Host=127.0.0.1;Port=1;Database=translation_only;Username=none;Password=none;Timeout=1";

    private const int PerSourceLimit = 750;

    /// <summary>
    /// The regression. <c>OpenedAtLocal</c> is the only wall-clock column on the fiscal day state and
    /// every other date beside it is a UTC instant, so any single expression spanning the two is refused
    /// by PostgreSQL before the query ever runs.
    /// </summary>
    [Fact]
    public Task FiscalDayLifecycleQueryTranslates() => AssertTranslatesAsync(
        (context, cancellationToken) => GetExceptionCenterHandler.LoadFiscalDayLifecycleFailuresAsync(
            context,
            // Built exactly as the handler builds it: the taxpayer's clock, because that is the clock the
            // column is stored in.
            AuditService.ToCAT(DateTime.UtcNow).AddHours(-30),
            PerSourceLimit,
            cancellationToken));

    /// <summary>
    /// The other fiscal source. It reads the sale rows, which carry the two remaining wall-clock columns.
    /// </summary>
    [Fact]
    public Task FiscalReceiptIngestQueryTranslates() => AssertTranslatesAsync(
        (context, cancellationToken) => GetExceptionCenterHandler.LoadFiscalReceiptIngestFailuresAsync(
            context, PerSourceLimit, cancellationToken));

    [Fact]
    public Task VanSalePostingQueryTranslates() => AssertTranslatesAsync(
        (context, cancellationToken) => GetExceptionCenterHandler.LoadVanSalePostingFailuresAsync(
            context,
            new VanSalesPostingSettings().WindowStart(VanSalesPostingSettings.CurrentTradingDate()),
            PerSourceLimit,
            cancellationToken));

    [Fact]
    public Task PendingTransferPostQueryTranslates() => AssertTranslatesAsync(
        (context, cancellationToken) => GetExceptionCenterHandler.LoadPendingTransferPostFailuresAsync(
            context, PerSourceLimit, cancellationToken));

    [Fact]
    public Task PendingRequestEditApplyQueryTranslates() => AssertTranslatesAsync(
        (context, cancellationToken) => GetExceptionCenterHandler.LoadPendingRequestEditApplyFailuresAsync(
            context, PerSourceLimit, cancellationToken));

    private static async Task AssertTranslatesAsync(Func<ApplicationDbContext, CancellationToken, Task> load)
    {
        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(NowhereConnectionString)
                .Options);

        var thrown = await Record.ExceptionAsync(() => load(context, CancellationToken.None));

        // There is no server behind the connection string, so the call cannot succeed. How far it got is
        // the whole question.
        Assert.NotNull(thrown);
        Assert.True(
            ReachedTheDatabase(thrown),
            "The query failed before the driver was ever asked to run it, so it never became SQL:"
                + Environment.NewLine + thrown);
    }

    /// <summary>
    /// True once the failure came from the driver, which only a fully translated query can reach. A query
    /// EF could not translate fails inside the translating visitor, so nothing Npgsql raises appears
    /// anywhere in its chain.
    /// </summary>
    private static bool ReachedTheDatabase(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException)
            {
                return true;
            }
        }

        return false;
    }
}
