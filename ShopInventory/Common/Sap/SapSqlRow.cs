using System.Globalization;

namespace ShopInventory.Common.Sap;

/// <summary>
/// Reads one cell out of a row returned by <c>SQLQueries('code')/List</c>.
/// </summary>
/// <remarks>
/// The executor hands back <see cref="object"/> cells typed by whatever the JSON parser made of
/// them, which for SAP means a number may arrive as a long, a decimal or a string, and a date
/// arrives as neither of the two formats general parsing accepts. Every caller therefore needs the
/// same four readers, and the date one in particular is not obvious enough to rewrite per feature:
/// SAP returns <c>yyyyMMdd</c> here, which <see cref="DateTime.TryParse(string, out DateTime)"/>
/// rejects outright, so a hand-rolled version silently dates every row 01/01/0001 and degrades the
/// sort that follows it into a no-op. That shipped once in the customer statement.
/// </remarks>
public static class SapSqlRow
{
    public static string? GetString(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? value?.ToString() : null;

    public static int GetInt32(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            decimal decimalValue => decimal.ToInt32(decimalValue),
            _ when int.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    public static decimal GetDecimal(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return 0m;
        }

        return value switch
        {
            decimal decimalValue => decimalValue,
            int intValue => intValue,
            long longValue => longValue,
            double doubleValue => Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture),
            _ when decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ when decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.CurrentCulture, out var fallback) => fallback,
            _ => 0m
        };
    }

    /// <summary>
    /// Returns <see cref="DateTime.MinValue"/> for a missing or unreadable cell — callers that need
    /// to tell that apart from a real date use <see cref="ToNullableDate"/>.
    /// </summary>
    public static DateTime GetDateTime(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return DateTime.MinValue;
        }

        if (value is DateTime dateTime)
        {
            return dateTime;
        }

        var text = value.ToString();

        // SAP returns dates from SQLQueries as yyyyMMdd — "20200821", not "2020-08-21".
        if (DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compact))
        {
            return compact.Date;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed.Date
            : DateTime.MinValue;
    }

    /// <summary>
    /// Distinguishes "SAP gave no date" from the <see cref="DateTime.MinValue"/>
    /// <see cref="GetDateTime"/> returns for a missing or unparseable cell, so an absent date is
    /// treated as unknown rather than as two thousand years ago.
    /// </summary>
    public static DateTime? ToNullableDate(DateTime value) =>
        value == DateTime.MinValue ? null : value;

    /// <summary>
    /// SAP accepts a bound date as <c>yyyy-MM-dd</c>. Its own <c>TO_DATE</c> is rejected by the
    /// SQLQueries validator, and <c>yyyyMMdd</c> — the format SAP hands dates *back* in — is
    /// accepted at create time and then silently matches nothing.
    /// </summary>
    public static string FormatDate(DateTime date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
