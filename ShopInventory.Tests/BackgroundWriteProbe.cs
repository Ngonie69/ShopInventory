using Microsoft.Data.Sqlite;
using ShopInventory.Data;

namespace ShopInventory.Tests;

/// <summary>
/// Polls for a write made by a fire-and-forget background task.
/// </summary>
/// <remarks>
/// Webhook delivery is fire-and-forget by design, so there is no handle to await — the only way to
/// see it is to look for what it wrote.
///
/// The retry on <see cref="SqliteException"/> is about the harness, not the code under test. These
/// tests share one in-memory SQLite connection between the polling context and the delivery task's
/// own scope, and SQLite will refuse a read taken while the writer holds the connection ("SQLite
/// Error 5"). Production runs on SQL Server with a real pool and has no such constraint. Swallowing
/// it here costs nothing: the deadline is unchanged, so a row that never lands still fails the
/// caller's assertion.
/// </remarks>
internal static class BackgroundWriteProbe
{
    public static async Task<T?> PollAsync<T>(
        Func<ApplicationDbContext> newContext,
        Func<ApplicationDbContext, Task<T?>> read,
        int timeoutSeconds = 5)
        where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var context = newContext();
                var found = await read(context);
                if (found != null)
                {
                    return found;
                }
            }
            catch (SqliteException)
            {
                // The delivery task has the connection. Look again in a moment.
            }

            await Task.Delay(50);
        }

        return null;
    }
}
