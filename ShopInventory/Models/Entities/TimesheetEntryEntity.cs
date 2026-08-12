using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Which operation a visit belongs to.
///
/// A merchandiser and a van sales rep still share this one table, but nothing above it is shared:
/// each has its own commands, handlers, queries, endpoint, permission, portal service and pages
/// (<c>Features/Timesheets</c> and <c>Features/VanSalesAttendance</c>). This column is what makes
/// that separation enforceable — every one of those queries pins it, none of them takes it as an
/// argument, so no caller can ask a merchandiser code path for a van's rows or the reverse.
///
/// Treat it as storage, not as a switch. If you find yourself adding a parameter that selects a
/// channel, the thing you actually want is the other feature folder.
///
/// Stamped at check-in rather than derived from the user's role at read time. Roles change; a rep
/// promoted off the vans would otherwise silently re-label every visit they ever made.
/// </summary>
public enum TimesheetChannel
{
    Merchandiser = 0,
    VanSales = 1
}

/// <summary>How much a recorded coordinate is worth. Mirrors the handset's own <c>LocationSource</c>.</summary>
public static class TimesheetLocationSources
{
    /// <summary>No fix — the coordinates are absent, not merely imprecise.</summary>
    public const string None = "None";

    /// <summary>A remembered fix, possibly minutes old and metres away.</summary>
    public const string LastKnown = "LastKnown";

    /// <summary>Taken from the GPS hardware at the moment of the event.</summary>
    public const string Gps = "Gps";
}

public class TimesheetEntryEntity
{
    [Key]
    public int Id { get; set; }

    public TimesheetChannel Channel { get; set; } = TimesheetChannel.Merchandiser;

    public Guid UserId { get; set; }

    [Required]
    [MaxLength(50)]
    public required string Username { get; set; }

    [Required]
    [MaxLength(50)]
    public required string CustomerCode { get; set; }

    [Required]
    [MaxLength(200)]
    public required string CustomerName { get; set; }

    public DateTime CheckInTime { get; set; }

    public DateTime? CheckOutTime { get; set; }

    public double? CheckInLatitude { get; set; }

    public double? CheckInLongitude { get; set; }

    public double? CheckOutLatitude { get; set; }

    public double? CheckOutLongitude { get; set; }

    [MaxLength(500)]
    public string? CheckInNotes { get; set; }

    [MaxLength(500)]
    public string? CheckOutNotes { get; set; }

    /// <summary>
    /// Duration in minutes, computed on check-out.
    /// </summary>
    public double? DurationMinutes { get; set; }

    // --- When the server was told, as distinct from when it happened ---
    //
    // CheckInTime and CheckOutTime above are the instants the rep actually tapped, which for an
    // offline visit is hours before the handset can say so. These record when the row was written.
    //
    // Keeping both is the whole point. The gap between them is the sync lag, so it is the only thing
    // that distinguishes a visit genuinely made at 09:00 from a back-dated claim — the server cannot
    // verify a client-supplied timestamp, but it can always state what it was told and when.

    public DateTime? CheckInRecordedAt { get; set; }

    public DateTime? CheckOutRecordedAt { get; set; }

    /// <summary>
    /// Whether either event reached the server materially later than it happened. Derived rather than
    /// stored so it cannot disagree with the timestamps it describes.
    /// </summary>
    [NotMapped]
    public bool WasCapturedOffline =>
        IsLate(CheckInTime, CheckInRecordedAt) || IsLate(CheckOutTime, CheckOutRecordedAt);

    // --- The handset's own name for each event ---
    //
    // A sync that reaches the server but loses the reply is the ordinary shape of a failure on a bad
    // line, and the handset's only safe response is to send it again. These make the second attempt
    // recognisable as the same visit. Unique, and nullable because nothing created on the web has one
    // — Postgres permits any number of nulls in a unique index, which is exactly the behaviour wanted.

    [MaxLength(100)]
    public string? CheckInClientReference { get; set; }

    [MaxLength(100)]
    public string? CheckOutClientReference { get; set; }

    // --- How far to trust the coordinates ---

    /// <summary>One of <see cref="TimesheetLocationSources"/>.</summary>
    [MaxLength(20)]
    public string? CheckInLocationSource { get; set; }

    [MaxLength(20)]
    public string? CheckOutLocationSource { get; set; }

    public double? CheckInLocationAccuracyMetres { get; set; }

    public double? CheckOutLocationAccuracyMetres { get; set; }

    /// <summary>
    /// Why an event has no coordinates at all. Diagnostic: one column for both events, so where both
    /// lacked a fix the later reason is the one kept — the two are the same cause in practice.
    /// </summary>
    [MaxLength(200)]
    public string? LocationUnavailableReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }

    /// <summary>
    /// Late enough to mean the record was queued rather than merely slow. A live request costs
    /// seconds; anything past a couple of minutes was waiting for signal.
    /// </summary>
    private static bool IsLate(DateTime? occurred, DateTime? recorded) =>
        occurred.HasValue && recorded.HasValue &&
        (recorded.Value - occurred.Value) > TimeSpan.FromMinutes(2);
}
