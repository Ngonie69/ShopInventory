using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Builds the SAP post guard the posting services take, for tests.
/// </summary>
/// <remarks>
/// Two of them, because the tests want two different things. <see cref="Backed"/> is the real guard
/// over a real store on the test's own SQLite connection, so a test can watch two attempts collide;
/// <see cref="Granting"/> models the uncontended case, for the many tests that are about what a
/// posting pass does rather than about who is allowed to start one.
/// </remarks>
internal static class SalePostGuards
{
    /// <summary>The production guard, over the store, on this test's database.</summary>
    public static IDesktopSalePostGuard Backed(SqliteConnection connection)
        => new DesktopSalePostGuard(
            new IdempotencyRequestStore(
                new ConnectionScopeFactory(connection),
                Options.Create(new SecuritySettings())),
            NullLogger<DesktopSalePostGuard>.Instance);

    /// <summary>
    /// Grants every claim and remembers nothing.
    /// </summary>
    /// <remarks>
    /// Not a shortcut around the guard: it is what "nobody else is posting this sale" looks like,
    /// which is the state every ordinary posting test means to be in. A test about contention builds
    /// <see cref="Backed"/> instead and gets the real thing.
    /// </remarks>
    public static IDesktopSalePostGuard Granting() => new AlwaysGrants();

    private sealed class AlwaysGrants : IDesktopSalePostGuard
    {
        public Task<DesktopSalePostClaim> ClaimAsync(
            DesktopSaleEntity sale, CancellationToken cancellationToken)
            => Task.FromResult(DesktopSalePostClaim.ForUnguarded(sale.ExternalReferenceId));
    }

    /// <summary>
    /// Hands the store a context on the test's own in-memory connection.
    /// </summary>
    /// <remarks>
    /// The store opens its own scope per call, deliberately, so that a claim commits independently
    /// of whatever transaction its caller is in. That needs a factory, and a test has no container —
    /// hence this, which is the same shape the other idempotency tests build inline.
    /// </remarks>
    private sealed class ConnectionScopeFactory(SqliteConnection connection)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly DbContextOptions<ApplicationDbContext> _options =
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;

        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
            => serviceType == typeof(ApplicationDbContext) ? new ApplicationDbContext(_options) : null;

        public void Dispose()
        {
        }
    }
}
