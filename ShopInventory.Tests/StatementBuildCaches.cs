using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Features.Statements;

namespace ShopInventory.Tests;

/// <summary>
/// The real cache over a private <see cref="MemoryCache"/>, so handler tests exercise the path
/// production uses without sharing cached statements with each other.
/// </summary>
internal static class StatementBuildCaches
{
    public static IStatementBuildCache Fresh() =>
        new StatementBuildCache(
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<StatementBuildCache>.Instance);
}
