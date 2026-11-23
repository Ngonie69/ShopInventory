using System.Globalization;
using System.Text.Json;

namespace ShopInventory.Common.Sap;

/// <summary>
/// Asks a stored query (<c>SQLQueries('code')/List</c>) for a decimal column as text, and reads it back.
/// </summary>
/// <remarks>
/// <para>
/// A Service Layer worker that writes a decimal into a stored query's answer can stall for good. Measured
/// on the SAP server on 22 Nov 2026 (Service Layer 10.00.250): the worker sits in
/// <c>B1SSqlService::OnAction → Json::StyledWriter::write → Json::valueToString(double, …) → snprintf</c>,
/// printing one value with a precision of about two billion digits. It fills an 8 GB buffer with
/// <c>0.000…</c>, fails, and starts again, at a full core, until the worker is killed. HANA has long since
/// answered. On 23 Nov 2026 thirteen such workers held 76.8 GB and the kernel killed HANA's index server
/// six times; at least ten of them were this API's <c>DSK_INV_COST_RANGE</c>.
/// </para>
/// <para>
/// Only a decimal goes that way: a count, a date and text never have. So every decimal column is selected
/// as <c>LEFT(T0."Quantity", 40) AS "Quantity"</c>, which the Service Layer hands back as a JSON string
/// and never formats as a number. It also comes back in full (<c>"11069.930000"</c>), where a decimal comes
/// back rounded to six significant figures.
/// </para>
/// <para>
/// <c>LEFT</c> is the only way to text. The Service Layer's SQL knows ISNULL, IFNULL, ABS, ROUND, FLOOR,
/// CEILING, LEFT, RIGHT, UPPER and LOWER, and refuses CAST, TO_VARCHAR, TO_NVARCHAR and <c>||</c>.
/// It takes <c>LEFT</c> over an aggregate too (<see cref="Sum"/>): tried on 23 Nov 2026 against the
/// test company, <c>LEFT(SUM(T0."LineTotal"), 40)</c> was accepted, stored as written, and answered
/// <c>"88.600000"</c>.
/// </para>
/// </remarks>
public static class SapSqlAmount
{
    /// <summary>Longer than any decimal SAP holds (a DECIMAL(21,6) is 23 characters with its sign and point).</summary>
    public const int TextLength = 40;

    /// <summary>
    /// The select-list item for one decimal column, under its own name:
    /// <c>LEFT(T0."Quantity", 40) AS "Quantity"</c>.
    /// </summary>
    public static string Column(string table, string column) => Column(table, column, column);

    /// <summary>
    /// The select-list item for one decimal column under another name:
    /// <c>LEFT(T1."OnHand", 40) AS "InStock"</c>.
    /// </summary>
    public static string Column(string table, string column, string alias) =>
        $"LEFT({table}.\"{column}\", {TextLength}) AS \"{alias}\"";

    /// <summary>
    /// The select-list item for a total, which is as much a decimal as the column it adds up:
    /// <c>LEFT(SUM(T1."LineTotal"), 40) AS "Revenue"</c>.
    /// </summary>
    public static string Sum(string table, string column, string alias) =>
        $"LEFT(SUM({table}.\"{column}\"), {TextLength}) AS \"{alias}\"";

    /// <summary>Several decimal columns of one table, each under its own name, comma-separated.</summary>
    public static string Columns(string table, params string[] columns) =>
        string.Join(", ", columns.Select(column => Column(table, column)));

    /// <summary>
    /// An amount of a stored-query row, whether SAP sent it as text (<see cref="Column(string, string)"/>)
    /// or as a number. A missing, null or empty cell is zero.
    /// </summary>
    /// <exception cref="SapAmountFormatException">The cell holds text that is not a number.</exception>
    public static decimal Read(JsonElement row, string column) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(column, out var value)
            ? Parse(value, column) ?? 0m
            : 0m;

    /// <summary>
    /// An amount of a stored-query row as <c>ExecuteRawSqlQueryAsync</c> hands it back: text, a
    /// <see cref="long"/> or a <see cref="decimal"/>. A missing, null or empty cell is zero.
    /// </summary>
    /// <exception cref="SapAmountFormatException">The cell holds text that is not a number.</exception>
    public static decimal Read(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) ? Parse(value, column) ?? 0m : 0m;

    /// <summary>
    /// As <see cref="Read(JsonElement, string)"/> for a cell already taken out of its row; null when the
    /// cell is null or empty.
    /// </summary>
    /// <exception cref="SapAmountFormatException">The cell holds text that is not a number.</exception>
    public static decimal? Parse(JsonElement value, string column) =>
        value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out var number)
                ? number
                : Convert.ToDecimal(value.GetDouble(), CultureInfo.InvariantCulture),
            JsonValueKind.String => ParseText(value.GetString(), column),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => throw new SapAmountFormatException(column, value.GetRawText())
        };

    /// <summary>As <see cref="Parse(JsonElement, string)"/> for a cell of a dictionary row.</summary>
    /// <exception cref="SapAmountFormatException">The cell holds text that is not a number.</exception>
    public static decimal? Parse(object? value, string column) =>
        value switch
        {
            null => null,
            decimal number => number,
            long number => number,
            int number => number,
            double number => Convert.ToDecimal(number, CultureInfo.InvariantCulture),
            string text => ParseText(text, column),
            JsonElement element => Parse(element, column),
            _ => throw new SapAmountFormatException(column, Convert.ToString(value, CultureInfo.InvariantCulture))
        };

    /// <summary>
    /// Invariant culture, and no thousands separators: SAP writes <c>-45678.120000</c>. Text that is not
    /// a number stops the read, because a zero in its place is a stock level or a balance nobody keyed.
    /// </summary>
    private static decimal? ParseText(string? text, string column)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? WithoutTrailingZeros(parsed)
            : throw new SapAmountFormatException(column, trimmed);
    }

    /// <summary>
    /// <c>5.000000</c> as <c>5</c>, which is what the same cell held when SAP sent it as a number. The
    /// value is the same either way; the scale is what a quantity is printed and serialised with.
    /// </summary>
    private static decimal WithoutTrailingZeros(decimal value) => value / 1.000000000000000000000000000000m;
}

/// <summary>
/// SAP answered a stored query with text where an amount belongs, and the text is not a number.
/// </summary>
/// <remarks>
/// Its own type so the row parsers that log and carry on past a malformed row can let this one through:
/// see the <c>when (ex is not SapAmountFormatException)</c> filters in <c>SAPServiceLayerClient</c>.
/// </remarks>
public sealed class SapAmountFormatException(string column, string? value)
    : FormatException($"SAP gave {column} as \"{value}\", which is not a number.")
{
    public string Column { get; } = column;
}
