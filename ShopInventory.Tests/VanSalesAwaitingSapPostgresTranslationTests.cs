using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Stock;
using ShopInventory.Data;

namespace ShopInventory.Tests;

/// <summary>
/// Compiles both halves of <see cref="VanSalesAwaitingSap"/> against the real PostgreSQL provider.
/// </summary>
/// <remarks>
/// The rest of the suite runs on SQLite, which translates a correlated subquery, an <c>IN</c> over a list
/// parameter and a nullable timestamp comparison its own way. These run on every page of every van's
/// catalogue read, so a query PostgreSQL cannot take would fail every van at once — caught here rather than
/// on a route. <c>ToQueryString</c> compiles without a connection, as
/// <see cref="ExceptionCenterPostgresTranslationTests"/> explains.
/// </remarks>
public sealed class VanSalesAwaitingSapPostgresTranslationTests
{
    private const string NowhereConnectionString =
        "Host=127.0.0.1;Port=1;Database=translation_only;Username=none;Password=none;Timeout=1";

    private static readonly DateTime Now = DateTime.UtcNow;

    private static ApplicationDbContext Postgres() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(NowhereConnectionString).Options);

    [Fact]
    public void The_uploaded_sales_query_translates()
    {
        using var context = Postgres();

        var sql = VanSalesAwaitingSap
            .UploadedQuery(context, "VAN004", ["YOG004", "YOG008"], Now.AddMinutes(-3), Now.AddDays(-30))
            .ToQueryString();

        Assert.Contains("GROUP BY", sql);
    }

    [Fact]
    public void The_reserved_sales_query_translates()
    {
        using var context = Postgres();

        var sql = VanSalesAwaitingSap
            .ReservedQuery(context, "VAN004", ["YOG004", "YOG008"], Now, Now.AddMinutes(-3), Now.AddDays(-30))
            .ToQueryString();

        Assert.Contains("GROUP BY", sql);
        Assert.Contains("EXISTS", sql);
    }
}
