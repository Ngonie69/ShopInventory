using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ShopInventory.Features.CustomerDocuments;

internal static class CustomerDocumentDbErrors
{
    /// <summary>
    /// Whether a save failed on a unique index covering <paramref name="column"/> — two people saving
    /// the same number at once, or a repeated request racing its first.
    /// </summary>
    /// <remarks>
    /// Matched on the constraint name in PostgreSQL. SQLite, which the unit tests run on, reports the
    /// same violation as a message naming the columns, so that is checked too; without it the race
    /// could not be tested outside a real database.
    /// </remarks>
    public static bool IsDuplicate(DbUpdateException exception, string column)
    {
        if (exception.InnerException is PostgresException postgres)
        {
            return postgres.SqlState == PostgresErrorCodes.UniqueViolation
                   && postgres.ConstraintName?.Contains(column, StringComparison.OrdinalIgnoreCase) == true;
        }

        var message = exception.InnerException?.Message;
        return message is not null
               && message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
               && message.Contains(column, StringComparison.OrdinalIgnoreCase);
    }
}
