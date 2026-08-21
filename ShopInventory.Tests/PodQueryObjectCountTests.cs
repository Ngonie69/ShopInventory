using System.Text.RegularExpressions;
using ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;
using ShopInventory.Features.Invoices.Queries.ValidateBulkPods;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the property that bounds OUQR for the four POD generators: each holds exactly one SAP
/// query object, whatever ranges or dates are asked for.
/// </summary>
/// <remarks>
/// These assert on the shape of the statement rather than on behaviour because the statement IS the
/// mechanism. A SAP query object is keyed on a hash of its text, so "how many objects can this path
/// create" is answered entirely by "how many distinct strings can this expression produce" — one,
/// if every varying value is bound, and one per distinct value set if any of them is interpolated.
///
/// Measured on production 2026-08-20, before the values were bound: 841 rows for the crate
/// classification, 587 for the credit-note links, 315 for credit-note activity and 115 for the
/// sales-order links. None of them can be deleted — DELETE against a large OUQR is killed by a
/// gateway timeout without committing — which is why this is worth a test rather than a comment.
/// </remarks>
public class PodQueryObjectCountTests
{
    /// <summary>
    /// A range predicate whose bounds are literals rather than <c>:name</c> parameters. This is the
    /// exact shape that made each of these paths mint a row per bucket.
    /// </summary>
    private static readonly Regex InterpolatedRangeBound =
        new(@"BETWEEN\s+'?\d", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A date compared against a literal rather than a bound parameter.
    /// </summary>
    private static readonly Regex InterpolatedDateBound =
        new(@"""DocDate""\s*[<>]=?\s*'", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static TheoryData<string, string, string[]> Statements => new()
    {
        {
            "crate invoice classification",
            GetPodUploadStatusHandler.CrateInvoiceClassificationSql,
            new[] { ":docEntryStart", ":docEntryEnd" }
        },
        {
            "credit note links by BaseEntry",
            GetPodUploadStatusHandler.CreditNoteLinkByEntrySql,
            new[] { ":rangeStart", ":rangeEnd" }
        },
        {
            "credit note links by BaseRef",
            GetPodUploadStatusHandler.CreditNoteLinkByRefSql,
            new[] { ":rangeStart", ":rangeEnd" }
        },
        {
            "credit note activity",
            GetPodUploadStatusHandler.CreditNoteActivitySql,
            new[] { ":fromDate", ":toDate" }
        },
        {
            "sales order links",
            ValidateBulkPodsHandler.SalesOrderLinkSql,
            new[] { ":docNumStart", ":docNumEnd" }
        }
    };

    [Theory]
    [MemberData(nameof(Statements))]
    public void Every_varying_value_is_bound_not_interpolated(
        string description,
        string sqlText,
        string[] expectedParameters)
    {
        foreach (var parameter in expectedParameters)
        {
            Assert.Contains(parameter, sqlText, StringComparison.Ordinal);
        }

        Assert.False(
            InterpolatedRangeBound.IsMatch(sqlText),
            $"The {description} statement compares a range against a literal. That makes its text "
            + "vary per request, and SAP mints an undeletable query object per distinct text.");

        Assert.False(
            InterpolatedDateBound.IsMatch(sqlText),
            $"The {description} statement compares DocDate against a literal date, which varies its "
            + "text per request. Bind the date instead; SAP accepts yyyy-MM-dd on the way in.");
    }

    /// <summary>
    /// Negative control: the detectors above fire on the statements as they actually stood when
    /// they were leaking.
    /// </summary>
    /// <remarks>
    /// Without this, every assertion in this file could be vacuous — a regex that matches nothing
    /// passes just as green against a broken statement as against a fixed one. These three literals
    /// are the historical shapes, byte-for-byte, that between them left 1,858 undeletable rows on
    /// production; if a detector stops recognising them it has stopped working.
    /// </remarks>
    [Theory]
    [InlineData(@"WHERE T0.""DocEntry"" BETWEEN 1105000 AND 1105999")]
    [InlineData(@"  AND T0.""BaseRef"" BETWEEN '1105' AND '9999'")]
    [InlineData(@"  AND T1.""DocDate"" >= '2026-01-01'")]
    public void The_detectors_fire_on_the_statements_that_were_leaking(string leakingFragment)
    {
        Assert.True(
            InterpolatedRangeBound.IsMatch(leakingFragment) || InterpolatedDateBound.IsMatch(leakingFragment),
            $"No detector recognised '{leakingFragment}', so the assertions in this file prove nothing.");
    }

    /// <summary>
    /// The two credit-note statements are generated from one template and unioned into one result
    /// set, so their projections have to stay identical — the reader looks up the same column names
    /// in rows from both.
    /// </summary>
    [Fact]
    public void The_two_credit_note_statements_differ_only_in_their_filter()
    {
        var byEntry = GetPodUploadStatusHandler.CreditNoteLinkByEntrySql.Split('\n');
        var byRef = GetPodUploadStatusHandler.CreditNoteLinkByRefSql.Split('\n');

        Assert.Equal(byEntry.Length, byRef.Length);

        var differing = byEntry
            .Zip(byRef, (left, right) => (Left: left, Right: right))
            .Where(pair => !string.Equals(pair.Left, pair.Right, StringComparison.Ordinal))
            .ToList();

        var difference = Assert.Single(differing);
        Assert.Contains("BaseEntry", difference.Left, StringComparison.Ordinal);
        Assert.Contains("BaseRef", difference.Right, StringComparison.Ordinal);
    }

    /// <summary>
    /// The codes have to be compile-time constants for the "one object" claim to hold: a code that
    /// carried request data would mint a row per request no matter how the values travelled.
    /// </summary>
    [Fact]
    public void Every_pod_query_code_is_a_fixed_string()
    {
        string[] codes =
        [
            GetPodUploadStatusHandler.CrateInvoiceClassificationQueryCode,
            GetPodUploadStatusHandler.CreditNoteLinkByEntryQueryCode,
            GetPodUploadStatusHandler.CreditNoteLinkByRefQueryCode,
            GetPodUploadStatusHandler.CreditNoteActivityQueryCode,
            ValidateBulkPodsHandler.SalesOrderLinkQueryCode
        ];

        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());

        foreach (var code in codes)
        {
            Assert.Matches("^[A-Z][A-Z0-9_]{2,49}$", code);

            // A 12-hex tail is what BuildContentAddressedQueryCode appends. Its presence here would
            // mean the path went back to a code derived from the statement, and with it the row-per-
            // distinct-text growth these constants exist to stop.
            Assert.DoesNotMatch("_[0-9A-F]{12}$", code);
        }
    }
}
