using System.Globalization;
using System.Text.RegularExpressions;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.InventoryTransfers;

/// <summary>Why an approved transfer has not reached SAP, as far as its record can say.</summary>
public static class PendingTransferFailureKinds
{
    /// <summary>SAP answered and the depot does not hold enough of one or more lines.</summary>
    public const string StockShort = "StockShort";

    /// <summary>SAP did not answer the stock read, so nothing was measured and nothing was posted.</summary>
    public const string StockUnread = "StockUnread";

    /// <summary>
    /// The post reached SAP and the answer was never read. The document may exist, so a blind retry
    /// can move the stock twice.
    /// </summary>
    public const string OutcomeUnknown = "OutcomeUnknown";

    /// <summary>Any other refusal.</summary>
    public const string Other = "Other";
}

/// <summary>One line the depot could not fill, read back out of a failed post's error.</summary>
public sealed record PendingTransferShortLine(
    string ItemCode,
    string? BatchNumber,
    string WarehouseCode,
    decimal RequestedQuantity,
    decimal AvailableQuantity)
{
    public decimal Shortage => Math.Max(0, RequestedQuantity - AvailableQuantity);
}

/// <summary>A failed post's cause and, for a shortage, the lines that caused it.</summary>
public sealed record PendingTransferFailure(
    string Kind,
    IReadOnlyList<PendingTransferShortLine> ShortLines,
    bool ShortLinesIncomplete);

/// <summary>
/// Reads <see cref="PendingInventoryTransferEntity.LastError"/> back into a cause.
/// </summary>
/// <remarks>
/// The poster writes a small set of fixed messages, so the error text is a dependable record of why a
/// post failed — and it is the only one the transfers that failed before this existed carry. Reading
/// it here, rather than adding a cause column, classifies those old failures too.
///
/// The four causes need four different responses: a shortage needs stock or a smaller document, an
/// unread warehouse needs SAP back, an unknown outcome needs somebody to look in SAP before anything
/// else, and the rest need a person to read the error.
/// </remarks>
public static partial class PendingTransferFailureClassifier
{
    /// <summary>
    /// The poster truncates <see cref="PendingInventoryTransferEntity.LastError"/> to this length.
    /// A message that long may have lost short lines off its end.
    /// </summary>
    public const int StoredErrorLength = 2000;

    public static PendingTransferFailure Classify(string? lastError)
    {
        if (string.IsNullOrWhiteSpace(lastError))
        {
            return new PendingTransferFailure(PendingTransferFailureKinds.Other, [], false);
        }

        if (lastError.Contains("timed out before SAP answered", StringComparison.OrdinalIgnoreCase))
        {
            return new PendingTransferFailure(PendingTransferFailureKinds.OutcomeUnknown, [], false);
        }

        if (lastError.StartsWith("Could not read stock from SAP", StringComparison.OrdinalIgnoreCase)
            || lastError.Contains("could not be read from SAP", StringComparison.OrdinalIgnoreCase))
        {
            return new PendingTransferFailure(PendingTransferFailureKinds.StockUnread, [], false);
        }

        if (lastError.StartsWith("Insufficient stock", StringComparison.OrdinalIgnoreCase))
        {
            var lines = ShortLinePattern().Matches(lastError)
                .Select(match => new PendingTransferShortLine(
                    ItemCode: match.Groups["item"].Value,
                    BatchNumber: match.Groups["batch"].Success ? match.Groups["batch"].Value : null,
                    WarehouseCode: match.Groups["warehouse"].Value,
                    RequestedQuantity: ParseQuantity(match.Groups["requested"].Value),
                    AvailableQuantity: ParseQuantity(match.Groups["available"].Value)))
                .ToList();

            return new PendingTransferFailure(
                PendingTransferFailureKinds.StockShort,
                lines,
                ShortLinesIncomplete: lastError.Length >= StoredErrorLength);
        }

        return new PendingTransferFailure(PendingTransferFailureKinds.Other, [], false);
    }

    // decimal.ToString() under the server's culture wrote these. The API runs invariant-ish (en), so
    // a thousands separator never appears, but a stray one must not turn 1,080 into 1.08.
    private static decimal ParseQuantity(string text) =>
        decimal.TryParse(text.Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;

    [GeneratedRegex(
        @"Insufficient stock for item '(?<item>[^']+)'(?: batch '(?<batch>[^']*)')? in warehouse '(?<warehouse>[^']+)'\. Requested: (?<requested>-?[\d.,]+), Available: (?<available>-?[\d.,]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ShortLinePattern();
}
