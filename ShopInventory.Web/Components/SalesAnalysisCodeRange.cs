using System.Globalization;
using System.Text.RegularExpressions;

namespace ShopInventory.Web.Components;

/// <summary>
/// Turning a written code range into the codes that actually exist, for
/// <see cref="SalesAnalysisPicker"/>'s two ways of asking for one.
/// </summary>
/// <remarks>
/// <para>
/// Every range is resolved against a list of real codes rather than expanded arithmetically, so it
/// can never name one that is not there. "BP0876 to BP1188" over a cache holding thirty-four of
/// those selects thirty-four, not the three hundred and thirteen the arithmetic would give — and
/// the count shown before the range is applied is therefore the count that will be applied.
/// </para>
/// <para>
/// Both ends are compared by number inside a prefix wherever both read as a prefix and a number,
/// because comparing them as text sorts BP9 after BP10. Where they do not — a bare letter, or two
/// ends of different prefixes — the codes are compared as text instead, which is what lets "A" to
/// "M" mean what it looks like.
/// </para>
/// </remarks>
public static class SalesAnalysisCodeRange
{
    /// <summary>
    /// "BP0876-1188", "van008 – 019", "VAN008 to 019" — a prefix, two numbers, and something
    /// between them. The prefix may be written once or twice, and the numbers may be the wrong way
    /// round.
    /// </summary>
    private static readonly Regex WrittenRegex = new(
        @"^(?<p1>[A-Za-z]*)\s*(?<n1>\d+)\s*(?:-|–|—|\.\.|to)\s*(?<p2>[A-Za-z]*)\s*(?<n2>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A code that can be spanned by number: a prefix and a number, nothing else.</summary>
    private static readonly Regex CodeRegex = new(
        @"^(?<prefix>[A-Za-z]+)(?<number>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>One end of a from / to pair: a code, or a bare number borrowing the other's prefix.</summary>
    private static readonly Regex BoundRegex = new(
        @"^(?<prefix>[A-Za-z]*)\s*(?<number>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The codes a range written as one string covers, or empty when the text is not a range or
    /// covers nothing.
    /// </summary>
    public static IReadOnlyList<string> FromWritten(string text, IEnumerable<string> codes)
    {
        var match = WrittenRegex.Match(text.Trim());
        if (!match.Success)
        {
            return Array.Empty<string>();
        }

        var prefix = match.Groups["p1"].Value is { Length: > 0 } first
            ? first
            : match.Groups["p2"].Value;

        if (!TryNumber(match.Groups["n1"].Value, out var start) ||
            !TryNumber(match.Groups["n2"].Value, out var end))
        {
            return Array.Empty<string>();
        }

        return ByNumber(prefix, Math.Min(start, end), Math.Max(start, end), codes);
    }

    /// <summary>
    /// The codes a from / to pair covers. Either end may be blank for an open range, so "from
    /// CRA001" and "up to VAN019" are both sayable; both blank covers nothing, since a picker
    /// already has its own way of saying everything.
    /// </summary>
    public static IReadOnlyList<string> FromBounds(string from, string to, IEnumerable<string> codes)
    {
        from = from.Trim();
        to = to.Trim();

        if (from.Length == 0 && to.Length == 0)
        {
            return Array.Empty<string>();
        }

        return NumericBounds(from, to) is { } bounds
            ? ByNumber(bounds.Prefix, bounds.Start, bounds.End, codes)
            : ByText(from, to, codes);
    }

    /// <summary>
    /// The two ends read as prefix-and-number, or null when one of them is not one — "A" to "M" is
    /// a range, but not a numeric one. A blank end is open and takes its prefix from the other, and
    /// a bare number does the same, so "TMP065" to "128" spans what it looks like it spans.
    /// </summary>
    private static (string Prefix, int Start, int End)? NumericBounds(string from, string to)
    {
        var low = from.Length == 0 ? null : ParseBound(from);
        var high = to.Length == 0 ? null : ParseBound(to);

        if ((from.Length > 0 && low is null) || (to.Length > 0 && high is null))
        {
            return null;
        }

        var lowPrefix = low?.Prefix ?? string.Empty;
        var highPrefix = high?.Prefix ?? string.Empty;

        // BP0876 to VAN019 is not a span of numbers — the codes themselves have to be compared.
        if (lowPrefix.Length > 0 && highPrefix.Length > 0 &&
            !string.Equals(lowPrefix, highPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var start = low?.Number ?? int.MinValue;
        var end = high?.Number ?? int.MaxValue;

        return (
            lowPrefix.Length > 0 ? lowPrefix : highPrefix,
            Math.Min(start, end),
            Math.Max(start, end));
    }

    private static (string Prefix, int Number)? ParseBound(string text)
    {
        var match = BoundRegex.Match(text);

        return match.Success && TryNumber(match.Groups["number"].Value, out var number)
            ? (match.Groups["prefix"].Value, number)
            : null;
    }

    /// <summary>
    /// Every code inside a numeric window of one prefix. An empty prefix takes them all, which is
    /// how a bare "876 to 1188" works, and codes that are not a prefix and a number are not in any
    /// numeric window.
    /// </summary>
    private static List<string> ByNumber(string prefix, int start, int end, IEnumerable<string> codes) =>
        codes
            .Select(code => new { Code = code, Parsed = CodeRegex.Match(code) })
            .Where(candidate => candidate.Parsed.Success)
            .Where(candidate => prefix.Length == 0 || string.Equals(
                candidate.Parsed.Groups["prefix"].Value,
                prefix,
                StringComparison.OrdinalIgnoreCase))
            .Select(candidate => new
            {
                candidate.Code,
                Number = int.Parse(candidate.Parsed.Groups["number"].Value, CultureInfo.InvariantCulture)
            })
            .Where(candidate => candidate.Number >= start && candidate.Number <= end)
            .OrderBy(candidate => candidate.Number)
            .ThenBy(candidate => candidate.Code, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Code)
            .ToList();

    /// <summary>
    /// Every code between two ends compared as text, for the ends that are not a number. The upper
    /// end takes everything beginning with it, so "A" to "M" reaches M001 rather than stopping just
    /// short of it.
    /// </summary>
    private static List<string> ByText(string from, string to, IEnumerable<string> codes) =>
        codes
            .Where(code => from.Length == 0 ||
                string.Compare(code, from, StringComparison.OrdinalIgnoreCase) >= 0)
            .Where(code => to.Length == 0 ||
                string.Compare(code, to, StringComparison.OrdinalIgnoreCase) <= 0 ||
                code.StartsWith(to, StringComparison.OrdinalIgnoreCase))
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool TryNumber(string text, out int number) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
}
