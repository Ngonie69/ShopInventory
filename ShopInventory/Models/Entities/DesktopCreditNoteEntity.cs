using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ShopInventory.Models.Entities;

/// <summary>A fiscal credit against a desktop receipt, independent of SAP document creation.</summary>
[Index(nameof(RequestKey), IsUnique = true)]
[Index(nameof(SaleId), nameof(Status))]
public sealed class DesktopCreditNoteEntity
{
    public Guid Id { get; set; }
    public int SaleId { get; set; }
    public DesktopSaleEntity Sale { get; set; } = null!;
    [MaxLength(32)] public string RequestKey { get; set; } = "";
    [MaxLength(64)] public string RequestHash { get; set; } = "";
    [MaxLength(50)] public string Number { get; set; } = "";
    [MaxLength(100)] public string OriginalFiscalNumber { get; set; } = "";
    [MaxLength(500)] public string Reason { get; set; } = "";
    [MaxLength(10)] public string Currency { get; set; } = "";
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    [MaxLength(30)] public string Status { get; set; } = DesktopCreditStatuses.Prepared;
    public string PlanJson { get; set; } = "";
    public string? FiscalResultJson { get; set; }
    public string? Message { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SubmitStartedAtUtc { get; set; }
    public DateTime? FiscalisedAtUtc { get; set; }

    // --- The back-office half ---
    //
    // Separate from Status above, and deliberately so: the two happen at different times and fail
    // independently. A sale is fiscalised the moment it is rung up and posts to SAP hours later, so a
    // credit raised at the counter is with ZIMRA at once and cannot reach SAP until the invoice it
    // reverses exists. One status could not say that, and "part done" is the state an operator most
    // needs told apart.
    //
    // Nothing here is attempted unless Status is "Fiscalised". A credit ZIMRA has not accepted must
    // not become a SAP document.

    public int? SapDocEntry { get; set; }
    public int? SapDocNum { get; set; }

    /// <summary>Deferred, Posted, Failed, NotRequired or ManualInSap — see <see cref="DesktopCreditSapStatuses"/>.</summary>
    [MaxLength(30)] public string SapStatus { get; set; } = DesktopCreditSapStatuses.Deferred;

    /// <summary>
    /// The reference SAP holds the credit memo under, in <c>NumAtCard</c>. The credit's own
    /// <see cref="Number"/>, so the two systems name one document the same way.
    /// </summary>
    /// <remarks>
    /// Without it a post whose reply was lost leaves a credit memo nothing can find, and the retry
    /// raises a second one — the shape that put duplicate invoices against single sales.
    /// </remarks>
    [MaxLength(100)] public string? SapReference { get; set; }

    /// <summary>
    /// When a post was last issued to SAP, whatever came back. Written and committed <i>before</i> the
    /// request goes out, like <see cref="DesktopSaleEntity.PostIssuedAtUtc"/>: it is the only local
    /// record that survives losing the reply.
    /// </summary>
    public DateTime? SapPostIssuedAtUtc { get; set; }

    public DateTime? SapPostedAt { get; set; }
    public int SapAttempts { get; set; }
    [MaxLength(2000)] public string? SapError { get; set; }

    /// <summary>Whether the credited units have been returned to the shared stock ledger.</summary>
    /// <remarks>
    /// Its own flag rather than inferred from <see cref="SapStatus"/>, because the ledger is returned
    /// to as soon as ZIMRA has the credit — hours before SAP may — and returning the same units twice
    /// would invent stock. See <c>DesktopCreditSapPoster</c>.
    /// </remarks>
    public bool UnitsReturnedToLedger { get; set; }
}

/// <summary>
/// The values <see cref="DesktopCreditNoteEntity.Status"/> takes — where the credit stands with ZIMRA.
/// </summary>
/// <remarks>
/// Named rather than spelled out at each use because the back-office half turns on exactly one of
/// them: nothing may be raised in SAP against a credit that is not <see cref="Fiscalised"/>, and a
/// literal mistyped in one place would quietly send a refused credit to SAP.
/// </remarks>
public static class DesktopCreditStatuses
{
    /// <summary>Saved, with its plan reserved, but not yet submitted to the device.</summary>
    public const string Prepared = "Prepared";

    /// <summary>Claimed by a submission in flight. Nothing else may submit it.</summary>
    public const string Submitting = "Submitting";

    /// <summary>ZIMRA holds the credit receipt. The only status the SAP half acts on.</summary>
    public const string Fiscalised = "Fiscalised";

    /// <summary>The device refused it before anything was filed; its reservation is released.</summary>
    public const string Rejected = "Rejected";

    /// <summary>
    /// The outcome is unknown and must be established by looking it up, never by submitting again.
    /// </summary>
    public const string ReconciliationRequired = "ReconciliationRequired";
}

/// <summary>
/// Where a fiscalised credit stands with SAP, and whether it will ever get there on its own.
/// </summary>
public static class DesktopCreditSapStatuses
{
    /// <summary>
    /// Owed, not yet raised — usually because the sale it reverses has not posted. Raised as soon as
    /// it does; see <c>DesktopCreditSapPoster</c> and the sweep behind it.
    /// </summary>
    public const string Deferred = "Deferred";

    public const string Posted = "Posted";

    /// <summary>SAP refused it, or could not be asked. Retried by the sweep.</summary>
    public const string Failed = "Failed";

    /// <summary>The sale is not going to SAP at all — it was excluded from posting.</summary>
    public const string NotRequired = "NotRequired";

    /// <summary>
    /// The sale reached SAP inside an end-of-day consolidated invoice, so it has no invoice of its own
    /// to credit and this side will not guess at one.
    /// </summary>
    /// <remarks>
    /// A consolidated invoice stands for many sales. A credit memo against it would have to be raised
    /// standalone, choosing batches for itself with nothing to take them from — and a batch-managed
    /// line with no batch selection fails the whole document. ZIMRA already holds the credit, which is
    /// the half nothing else can do; this says plainly that the rest is a person's job.
    /// </remarks>
    public const string ManualInSap = "ManualInSap";
}
