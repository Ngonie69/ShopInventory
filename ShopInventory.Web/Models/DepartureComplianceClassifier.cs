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

            // The vehicle's departure where there is one, the handset's where there is not.
            // This is the whole point of reading the truck: a rep who taps Start Day at 06:55 and
            // leaves at 07:40 was recorded as on time, and nothing in the system disagreed.
            if (day.EffectiveDeparture is { } departed && departed.TimeOfDay > LatestDeparture)
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
    public static (string Label, FlagTone Tone)? PrimaryFlag(Gaps gaps, Signals signals = Signals.None)
    {
        if (gaps.HasFlag(Gaps.NoDeparture)) return ("No departure record", FlagTone.Strong);
        if (gaps.HasFlag(Gaps.NothingDeclared)) return ("Nothing declared", FlagTone.Strong);
        if (gaps.HasFlag(Gaps.ShortDeclaration)) return ("Short declaration", FlagTone.Strong);
        // A rep who counts back more than the day can account for is as much a finding as one who
        // counts back less — an unrecorded sale, most often — and carried no badge at all before.
        if (gaps.HasFlag(Gaps.OverDeclaration)) return ("Over declaration", FlagTone.Strong);
        if (gaps.HasFlag(Gaps.LateOut)) return ("Late out", FlagTone.Warn);
        if (gaps.HasFlag(Gaps.NoOdometer)) return ("No odometer", FlagTone.Warn);

        // Below the gaps, and never in their ink. A clean day that the vehicle could not answer
        // for is still a clean day; the badge says only that nothing checked it.
        if (signals.HasFlag(Signals.VehicleDidNotMove)) return ("Vehicle never moved", FlagTone.Info);
        if (signals.HasFlag(Signals.VehicleUnmatched)) return ("Truck not in fleet", FlagTone.Info);
        if (signals.HasFlag(Signals.NoTelematics)) return ("Not verified", FlagTone.Info);

        return null;
    }

    /// <summary>
    /// How far the handset and the vehicle may disagree before the row says so.
    /// </summary>
    /// <remarks>
    /// A threshold, so policy, so it sits with the others. Ten minutes because the vehicle's
    /// departure is the first position fix past the depot radius and the rep taps Start Day at
    /// the wheel: a few minutes between the two is the system working, not a finding.
    /// </remarks>
    public const int DiscrepancyToleranceMinutes = 10;

    /// <summary>
    /// The smallest odometer gap worth showing, and the smallest share of the day worth showing.
    /// </summary>
    /// <remarks>
    /// Both, because a 4 km gap on a 20 km round is a different thing from a 4 km gap on a 350 km
    /// one. Expect the vehicle to read slightly higher as a matter of course — it accumulates
    /// metres while the rep subtracts two whole-kilometre readings.
    /// </remarks>
    public const int OdometerToleranceKm = 5;

    /// <inheritdoc cref="OdometerToleranceKm"/>
    public const int OdometerTolerancePercent = 10;

    /// <summary>
    /// What is worth knowing about a rep-day that is not a finding against the rep.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Gaps"/> and never counted into it. "Compliant" means
    /// <c>Gaps == Gaps.None</c>, so a signal added to that enum would change who passes — and
    /// none of these should. A truck nobody assigned is an administrator's omission; a vehicle
    /// that disagrees with a handset is a question; a tracker in the workshop is a fact about
    /// the fleet. A supervisor should see all three and none of them should mark a rep.
    /// </remarks>
    public static Signals SignalsOf(DepartureComplianceDay day)
    {
        var signals = Signals.None;
        var vehicle = day.Telematics;

        if (vehicle is null)
        {
            return signals;
        }

        if (day.DepartureIsVerified)
        {
            signals |= Signals.DepartureVerified;
        }

        switch (vehicle.Match)
        {
            case TelematicsMatch.NoRegistration:
                signals |= Signals.VehicleNoRegistration;
                break;

            case TelematicsMatch.NotInFleet:
                signals |= Signals.VehicleUnmatched;
                break;

            case TelematicsMatch.Matched when !vehicle.HasRollup:
                signals |= Signals.NoTelematics;
                break;

            case TelematicsMatch.Matched when vehicle.DidNotMove:
                // Not a gap. A van that never started is as likely to be a dead tracker as an
                // idle driver, and the report must not decide which on the rep's behalf.
                signals |= Signals.VehicleDidNotMove;
                break;
        }

        if (day.DepartureDiscrepancyMinutes is { } delta
            && Math.Abs(delta) >= DiscrepancyToleranceMinutes)
        {
            signals |= Signals.DepartureDiscrepancy;
        }

        if (vehicle.DistanceKm is not null && day.KilometresTravelled is null)
        {
            signals |= Signals.OdometerFromVehicle;
        }

        if (day.OdometerDivergenceKm is { } divergence
            && day.KilometresTravelled is { } captured
            && Math.Abs(divergence) >= OdometerToleranceKm
            && Math.Abs(divergence) >= captured * OdometerTolerancePercent / 100)
        {
            signals |= Signals.OdometerDivergence;
        }

        if (vehicle.OdometerReset || vehicle.TerminalChanged)
        {
            signals |= Signals.OdometerUnreliable;
        }

        if (vehicle.VehicleStateLabel is not null)
        {
            signals |= Signals.VehicleOffRoad;
        }

        return signals;
    }

    /// <summary>The exception chips, in the order they are shown.</summary>
    public static readonly GapFilter[] Filters =
    [
        new(AllRepDays, "All rep-days", _ => true),
        new("departure", "No departure record", row => row.Gaps.HasFlag(Gaps.NoDeparture)),
        new("money", "Money not accounted for",
            row => row.Gaps.HasFlag(Gaps.NothingDeclared)
                   || row.Gaps.HasFlag(Gaps.ShortDeclaration)
                   || row.Gaps.HasFlag(Gaps.OverDeclaration)),
        new("ccr", "CCR under target", row => row.Gaps.HasFlag(Gaps.CcrUnderTarget)),
        new("pcr", "PCR under target", row => row.Gaps.HasFlag(Gaps.PcrUnderTarget)),
        new("odometer", "No odometer", row => row.Gaps.HasFlag(Gaps.NoOdometer))
    ];

    /// <summary>
    /// The vehicle chips, shown as their own group.
    /// </summary>
    /// <remarks>
    /// Kept apart from the exception chips above rather than appended to them, so a reader can
    /// see at a glance which chips narrow to a finding against a rep and which narrow to
    /// something about the data. None of these affects whether a day counts as compliant.
    /// </remarks>
    public static readonly GapFilter[] VehicleFilters =
    [
        new("unverified", "Departure not verified",
            row => row.Signals.HasFlag(Signals.NoTelematics)
                   || row.Signals.HasFlag(Signals.VehicleUnmatched)
                   || row.Signals.HasFlag(Signals.VehicleNoRegistration)),
        new("disputed", "Out earlier or later than recorded",
            row => row.Signals.HasFlag(Signals.DepartureDiscrepancy)),
        new("didnotmove", "Vehicle never moved",
            row => row.Signals.HasFlag(Signals.VehicleDidNotMove)),
        new("mileage", "Mileage disagrees",
            row => row.Signals.HasFlag(Signals.OdometerDivergence))
    ];

    /// <summary>Every chip, in the order the page renders them.</summary>
    public static IEnumerable<GapFilter> AllFilters => Filters.Concat(VehicleFilters);

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

    /// <summary>
    /// Every signal on a row, for the export's own column — kept separate from
    /// <see cref="FlagList"/> so "Flags" keeps meaning compliance findings.
    /// </summary>
    public static string SignalList(Signals signals)
    {
        if (signals == Signals.None)
        {
            return string.Empty;
        }

        var names = new List<string>();

        if (signals.HasFlag(Signals.DepartureVerified)) names.Add("Verified by vehicle");
        if (signals.HasFlag(Signals.DepartureDiscrepancy)) names.Add("Departure disputed");
        if (signals.HasFlag(Signals.VehicleDidNotMove)) names.Add("Vehicle never moved");
        if (signals.HasFlag(Signals.NoTelematics)) names.Add("No telematics");
        if (signals.HasFlag(Signals.VehicleUnmatched)) names.Add("Truck not in fleet");
        if (signals.HasFlag(Signals.VehicleNoRegistration)) names.Add("No truck assigned");
        if (signals.HasFlag(Signals.VehicleOffRoad)) names.Add("Vehicle off road");
        if (signals.HasFlag(Signals.OdometerFromVehicle)) names.Add("Mileage from vehicle only");
        if (signals.HasFlag(Signals.OdometerDivergence)) names.Add("Mileage disagrees");
        if (signals.HasFlag(Signals.OdometerUnreliable)) names.Add("Odometer unreliable");

        return string.Join("; ", names);
    }
}

/// <summary>A rep-day with its findings worked out once.</summary>
public sealed record ComplianceRow(DepartureComplianceDay Day, Gaps Gaps, Signals Signals);

/// <summary>
/// One chip: its key in the query, its label, and what it keeps.
/// </summary>
/// <remarks>
/// Takes the whole row rather than just the gaps, so a chip can narrow on a signal as easily as
/// on a finding. The alternative was a second chip type with its own plumbing, for no gain.
/// </remarks>
public sealed record GapFilter(string Key, string Label, Func<ComplianceRow, bool> Test);

/// <summary>
/// How loudly a badge is drawn. A finding against a rep is not the same as a note about the data,
/// and they must not look the same.
/// </summary>
public enum FlagTone
{
    /// <summary>A finding worth acting on today — money, or a day with no record at all.</summary>
    Strong,

    /// <summary>A finding, but a softer one: late out, no odometer.</summary>
    Warn,

    /// <summary>Not a finding. Something about the data, or about the vehicle.</summary>
    Info
}

/// <summary>
/// What is worth knowing about a rep-day that is not a finding against the rep.
/// </summary>
/// <remarks>
/// Deliberately a separate enum from <see cref="Gaps"/>. "Compliant" is
/// <c>Gaps == Gaps.None</c>, so anything added there changes who passes — and none of these
/// should. They are shown, and they are filterable, and they mark nobody.
/// </remarks>
[Flags]
public enum Signals
{
    None = 0,

    /// <summary>A vehicle, not a handset, answered for this departure.</summary>
    DepartureVerified = 1,

    /// <summary>The vehicle and the handset disagree about when the van left.</summary>
    DepartureDiscrepancy = 2,

    /// <summary>Matched to a vehicle, which has no data for this day.</summary>
    NoTelematics = 4,

    /// <summary>A truck is named but the fleet does not hold it — a typo, or a vehicle sold.</summary>
    VehicleUnmatched = 8,

    /// <summary>No truck is named on the day or on the rep's route.</summary>
    VehicleNoRegistration = 16,

    /// <summary>The vehicle reported and never left the depot.</summary>
    VehicleDidNotMove = 32,

    /// <summary>The rep recorded no mileage, but the vehicle has a distance.</summary>
    OdometerFromVehicle = 64,

    /// <summary>The rep's distance and the vehicle's differ by more than the tolerance.</summary>
    OdometerDivergence = 128,

    /// <summary>The odometer was reset or the tracker swapped, so the distance means little.</summary>
    OdometerUnreliable = 256,

    /// <summary>In the workshop, or the tracker is — which is why it may report nothing.</summary>
    VehicleOffRoad = 512
}

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
