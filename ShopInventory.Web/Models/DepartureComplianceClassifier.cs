namespace ShopInventory.Web.Models;

/// <summary>
/// What is wrong with a rep-day, and the policy that decides it.
/// </summary>
/// <remarks>
/// <para>
/// This is the compliance page's rulebook, lifted out of the page so it can be tested. It decided
/// who passed for as long as it lived in a <c>@code</c> block, where nothing could reach it: eight
/// rules and three policy figures, none of them covered by a test, in a 1,258-line file. Extracted
/// unchanged first so that the changes which follow are provable — a regression in a rule and a
/// regression in the markup around it are indistinguishable otherwise.
/// </para>
/// <para>
/// It stays in this project rather than moving to the API because these are judgements about an
/// already-computed fact set, not facts. Every input <see cref="GapsOf"/> reads is a property the
/// API already sent. Moving the bars to the server would mean an API deploy to change a percentage
/// a supervisor sets, and the enum would have to exist here anyway because the exception chips
/// filter on it — so the taxonomy would end up in both places, which is worse than in one. If a
/// second consumer ever appears, moving a tested class is mechanical.
/// </para>
/// </remarks>
public static class DepartureComplianceClassifier
{
    /// <summary>
    /// The targets the rates are scored against.
    ///
    /// They are policy rather than measurement, which is why they are stated here as two named
    /// numbers instead of being buried in the comparisons: a supervisor who wants the bar moved
    /// should have one place to move it. They are deliberately not equal — all but one of the
    /// planned calls should be made, while converting three in four of them is a good day.
    ///
    /// Both are above the design's own defaults of 90% and 55%, which were set aside as too
    /// soft: on this route book more than half the calls buying is an ordinary day, and a
    /// column that marks an ordinary day marks nothing.
    ///
    /// Both also sit inside the range the meter this page replaced already used, which had
    /// two lines per rate rather than one: 95/85 for the CCR and 90/75 for the PCR. So the
    /// CCR is now held to that meter's "good round" exactly, and the PCR to its fair/poor
    /// line. What the rebuild changes is the shape rather than the standard — a rate is
    /// either at its bar or under it, instead of being sorted into good, fair and poor.
    /// </summary>
    public const double CcrTarget = 0.95;

    /// <inheritdoc cref="CcrTarget"/>
    public const double PcrTarget = 0.75;

    /// <summary>
    /// After this, a departure counts as late. Also policy, and also the design's figure: the vans
    /// are loaded overnight to leave at first light, and a round that starts at eight arrives at
    /// the far end of the route after the shops have taken their morning delivery from someone else.
    /// </summary>
    public static readonly TimeSpan LatestDeparture = new(7, 0, 0);

    /// <summary>The chip key meaning no exception filter is applied.</summary>
    public const string AllRepDays = "all";

    /// <summary>
    /// What is wrong with a rep-day, if anything.
    /// </summary>
    /// <remarks>
    /// A missing declaration is only a gap where money came in: a rep who was on the road all day
    /// and sold nothing has nothing to declare, and flagging that would put a mark on the one row
    /// where the takings and the declaration already agree. The same reasoning keeps the odometer
    /// and the late departure off a day with no departure record at all — there was no Start Day to
    /// carry either, which is what "No departure record" already says.
    /// </remarks>
    public static Gaps GapsOf(DepartureComplianceDay day)
    {
        var gaps = Gaps.None;

        if (!day.HasDayRecord)
        {
            gaps |= Gaps.NoDeparture;
        }
        else
        {
            if (day.KilometresTravelled is null)
            {
                gaps |= Gaps.NoOdometer;
            }

            if (day.TimeOut is { } departed && departed.TimeOfDay > LatestDeparture)
            {
                gaps |= Gaps.LateOut;
            }
        }

        if (day.SystemTotalSales > 0 && day.DeclaredTotal is null)
        {
            gaps |= Gaps.NothingDeclared;
        }

        // Both read from the API's own definitions rather than from the raw variance. A rep whose day
        // included a sale with no tender on it used to be marked short by exactly that sale's value —
        // money the handset gave them no box to declare. The shortfall is now measured against the
        // takings they could declare, and the overage allows every untendered sale to have been cash
        // they collected before it calls anything unaccounted for.
        if (day.DeclaredShortfall is not null)
        {
            gaps |= Gaps.ShortDeclaration;
        }

        if (day.DeclaredOverage is not null)
        {
            gaps |= Gaps.OverDeclaration;
        }

        if (day.CallComplianceRate is { } ccr && ccr < CcrTarget)
        {
            gaps |= Gaps.CcrUnderTarget;
        }

        if (day.ProductiveCallRate is { } pcr && pcr < PcrTarget)
        {
            gaps |= Gaps.PcrUnderTarget;
        }

        return gaps;
    }

    /// <summary>
    /// The one badge a row carries, in a stated order of precedence. A rate under target never
    /// becomes the badge: the figure itself is already marked, in its own column, next to the
    /// numbers that explain it.
    /// </summary>
    public static (string Label, bool Strong)? PrimaryFlag(Gaps gaps)
    {
        if (gaps.HasFlag(Gaps.NoDeparture)) return ("No departure record", true);
        if (gaps.HasFlag(Gaps.NothingDeclared)) return ("Nothing declared", true);
        if (gaps.HasFlag(Gaps.ShortDeclaration)) return ("Short declaration", true);
        // A rep who counts back more than the day can account for is as much a finding as one who
        // counts back less — an unrecorded sale, most often — and carried no badge at all before.
        if (gaps.HasFlag(Gaps.OverDeclaration)) return ("Over declaration", true);
        if (gaps.HasFlag(Gaps.LateOut)) return ("Late out", false);
        if (gaps.HasFlag(Gaps.NoOdometer)) return ("No odometer", false);

        return null;
    }

    /// <summary>The exception chips, in the order they are shown.</summary>
    public static readonly GapFilter[] Filters =
    [
        new(AllRepDays, "All rep-days", _ => true),
        new("departure", "No departure record", gaps => gaps.HasFlag(Gaps.NoDeparture)),
        new("money", "Money not accounted for",
            gaps => gaps.HasFlag(Gaps.NothingDeclared)
                    || gaps.HasFlag(Gaps.ShortDeclaration)
                    || gaps.HasFlag(Gaps.OverDeclaration)),
        new("ccr", "CCR under target", gaps => gaps.HasFlag(Gaps.CcrUnderTarget)),
        new("pcr", "PCR under target", gaps => gaps.HasFlag(Gaps.PcrUnderTarget)),
        new("odometer", "No odometer", gaps => gaps.HasFlag(Gaps.NoOdometer))
    ];

    /// <summary>Every gap on a row, for the export's one column. Blank when the row is clean.</summary>
    public static string FlagList(Gaps gaps)
    {
        if (gaps == Gaps.None)
        {
            return string.Empty;
        }

        var names = new List<string>();

        if (gaps.HasFlag(Gaps.NoDeparture)) names.Add("No departure record");
        if (gaps.HasFlag(Gaps.NothingDeclared)) names.Add("Nothing declared");
        if (gaps.HasFlag(Gaps.ShortDeclaration)) names.Add("Short declaration");
        if (gaps.HasFlag(Gaps.OverDeclaration)) names.Add("Over declaration");
        if (gaps.HasFlag(Gaps.LateOut)) names.Add("Late out");
        if (gaps.HasFlag(Gaps.NoOdometer)) names.Add("No odometer");
        if (gaps.HasFlag(Gaps.CcrUnderTarget)) names.Add("CCR under target");
        if (gaps.HasFlag(Gaps.PcrUnderTarget)) names.Add("PCR under target");

        return string.Join("; ", names);
    }
}

/// <summary>A rep-day with its gaps worked out once.</summary>
public sealed record ComplianceRow(DepartureComplianceDay Day, Gaps Gaps);

/// <summary>One exception chip: its key in the query, its label, and what it keeps.</summary>
public sealed record GapFilter(string Key, string Label, Func<Gaps, bool> Test);

/// <summary>
/// The findings a rep-day can carry. A day with none of them is what the page counts as compliant,
/// so adding a member here changes who passes.
/// </summary>
[Flags]
public enum Gaps
{
    None = 0,
    NoDeparture = 1,
    NothingDeclared = 2,
    ShortDeclaration = 4,
    LateOut = 8,
    NoOdometer = 16,
    CcrUnderTarget = 32,
    PcrUnderTarget = 64,
    OverDeclaration = 128
}
