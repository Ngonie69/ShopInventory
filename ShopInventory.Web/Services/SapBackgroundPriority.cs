namespace ShopInventory.Web.Services;

/// <summary>
/// Marks the API calls a fire-and-forget cache sweep makes as background work, so the API keeps
/// them out of the SAP slots it reserves for people.
/// </summary>
/// <remarks>
/// The API limits SAP concurrency to six slots and keeps two of them for interactive requests —
/// but it treats every inbound request as interactive unless told otherwise. The cache services
/// here walk <c>/paged</c> endpoints page after page from <c>Task.Run</c>, and those endpoints
/// also serve the first page a person is waiting on, so the API cannot tell the two apart by
/// endpoint. A sweep arriving as interactive could take all six slots, reservation included.
/// <para>
/// The mark is ambient (an <see cref="AsyncLocal{T}"/>) rather than a parameter, so it covers every
/// request a sweep makes, however deep, and nothing it does not: a value set inside a
/// <c>Task.Run</c> never flows back to the circuit that started it. It is applied by
/// <see cref="SapBackgroundPriorityHandler"/> in the API clients' pipeline. Unlike
/// <see cref="ClientAuditHeaders"/>, that works from a handler: the handler runs in the caller's
/// execution context, so it sees the caller's <see cref="AsyncLocal{T}"/> even though it is built
/// in the client factory's own DI scope.
/// </para>
/// </remarks>
public static class SapBackgroundPriority
{
    /// <summary>Must match <c>SapRequestPriorityMiddleware.HeaderName</c> in the API.</summary>
    public const string HeaderName = "X-Sap-Priority";

    /// <summary>Must match <c>SapRequestPriorityMiddleware.BackgroundValue</c> in the API.</summary>
    public const string BackgroundValue = "background";

    private static readonly AsyncLocal<bool> Background = new();

    /// <summary>True inside <see cref="Begin"/> or <see cref="Run"/>.</summary>
    public static bool IsBackground => Background.Value;

    /// <summary>
    /// Marks every API request made until the returned scope is disposed as background work.
    /// </summary>
    public static IDisposable Begin()
    {
        var previous = Background.Value;
        Background.Value = true;
        return new Scope(previous);
    }

    /// <summary>
    /// <c>Task.Run</c> for a cache sweep nobody is waiting on: runs <paramref name="work"/> on the
    /// thread pool with its API requests marked background.
    /// </summary>
    public static Task Run(Func<Task> work) =>
        Task.Run(async () =>
        {
            using var background = Begin();
            await work();
        });

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Background.Value = previous;
    }
}
