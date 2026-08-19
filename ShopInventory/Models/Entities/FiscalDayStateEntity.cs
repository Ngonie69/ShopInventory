using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>
/// How far one device's fiscal day has got towards ZIMRA.
/// </summary>
/// <remarks>
/// A receipt stamped on a handset is not with ZIMRA when the platform archives it. It is with ZIMRA when
/// the fiscal day it belongs to is closed, packaged into an offline file, and that file is submitted and
/// accepted. Those are four separate operations, any of which can fail or — worse — return without
/// saying whether it happened.
///
/// The platform records each step for itself, but it is the wrong place to ask: an indeterminate close
/// looks the same there as one that never started, and the taxpayer's deadline is measured against the
/// day's opening time, which only the device knows. So the sequence is tracked here, one row per device
/// per day, and this is what the console reads and what the scheduled close resumes from.
/// </remarks>
[Table("FiscalDayStates")]
[Index(nameof(DeviceId), nameof(FiscalDayNo), IsUnique = true)]
// The scheduled run asks "which days are not finished", across every device, on every pass.
[Index(nameof(Status))]
public class FiscalDayStateEntity
{
    [Key]
    public int Id { get; set; }

    public int DeviceId { get; set; }

    public int FiscalDayNo { get; set; }

    /// <summary>
    /// When the device opened this day, in the taxpayer's wall clock with no offset.
    /// </summary>
    /// <remarks>
    /// Stored without a time zone for the same reason the receipt date is: the day's length is measured
    /// against it in local terms, and converting it moves the deadline. See
    /// <see cref="DesktopSaleEntity.ReceiptDate"/>.
    /// </remarks>
    [Column(TypeName = "timestamp without time zone")]
    public DateTime? OpenedAtLocal { get; set; }

    /// <summary>The taxpayer's maximum day length as the device reported it, in hours.</summary>
    /// <remarks>
    /// Copied per day rather than read from settings, because it is a ZIMRA-side value that can change,
    /// and a day already open must be judged against the limit it opened under.
    /// </remarks>
    public int? MaxDurationHours { get; set; }

    public FiscalDayLifecycleStatus Status { get; set; } = FiscalDayLifecycleStatus.Open;

    public DateTime? ClosedAtUtc { get; set; }

    public DateTime? FileGeneratedAtUtc { get; set; }

    public DateTime? FileSubmittedAtUtc { get; set; }

    /// <summary>The platform's identifier for the submitted offline file, for reconciling it later.</summary>
    [MaxLength(100)]
    public string? OfflineFileReference { get; set; }

    /// <summary>Receipts this application handed to the platform for this day.</summary>
    /// <remarks>
    /// Our count, not ZIMRA's. A mismatch against the file's own count is the signal that a receipt was
    /// signed and never arrived — which is precisely what a closed day can no longer accept.
    /// </remarks>
    public int IngestedReceiptCount { get; set; }

    /// <summary>How many times the lifecycle has been advanced for this day.</summary>
    public int Attempts { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    /// <summary>
    /// The warning that this day is approaching the taxpayer's limit has already been raised.
    /// </summary>
    /// <remarks>
    /// The scheduled run passes far more often than the warning should fire, and an alert that repeats
    /// every few minutes is one people learn to close without reading.
    /// </remarks>
    public bool DurationWarningRaised { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether ZIMRA has this day's receipts.</summary>
    public bool IsComplete => Status == FiscalDayLifecycleStatus.Submitted;

    /// <summary>Whether the lifecycle has stopped somewhere a person has to look at.</summary>
    public bool NeedsAttention =>
        Status is FiscalDayLifecycleStatus.NeedsReconciliation or FiscalDayLifecycleStatus.Failed;
}

/// <summary>
/// The steps between a receipt being stamped and ZIMRA holding it, in order.
/// </summary>
public enum FiscalDayLifecycleStatus
{
    /// <summary>Trading. Receipts are still being signed into this day.</summary>
    Open = 0,

    /// <summary>
    /// Every receipt this application knows about has been archived by the platform, so the day can be
    /// closed without stranding one.
    /// </summary>
    Drained = 1,

    /// <summary>Closed at FDMS. No further receipt can join this day.</summary>
    Closed = 2,

    /// <summary>The offline file exists at the platform and is waiting to be sent.</summary>
    FileGenerated = 3,

    /// <summary>Submitted and accepted. ZIMRA has the day's receipts; this is the finish line.</summary>
    Submitted = 4,

    /// <summary>
    /// An operation returned without saying whether it took effect. It must be resolved by reading the
    /// device's status or the platform's submitted-file list, never by repeating the operation.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Failed"/> because the safe action is the opposite one. A failure can be
    /// retried; this cannot — closing a day twice, or submitting a file twice, is not idempotent at
    /// FDMS.
    /// </remarks>
    NeedsReconciliation = 5,

    /// <summary>
    /// FDMS itself refused this day's file, for a stated reason.
    /// </summary>
    /// <remarks>
    /// Not a synonym for "gave up". A day that merely runs out of attempts — which is what a platform
    /// outage looks like — is paused and given another set of attempts once the pause elapses, because
    /// parking it here permanently means its receipts never reach ZIMRA at all.
    ///
    /// This is the other thing: FDMS processed the file and said no, so nothing automatic touches the day
    /// again. The only automated response available would be to package and upload once more, and FDMS
    /// already holds the receipts the rejected file carried. A person rebuilds the day on the platform's
    /// Offline Files page and then puts it back with
    /// <c>FiscalDayLifecycleService.ResumeFailedDayAsync</c>, which resumes it at the furthest step it
    /// actually reached.
    /// </remarks>
    Failed = 6
}
