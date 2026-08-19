using ErrorOr;
using MediatR;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalDayStates;

/// <summary>
/// How far each device's fiscal day has got towards ZIMRA, newest first.
/// </summary>
/// <remarks>
/// <c>Status</c> is a <c>FiscalDayLifecycleStatus</c> name, or <c>needs-attention</c> for the two that
/// have stopped somewhere a person has to look at. Null for every day.
/// </remarks>
public sealed record GetFiscalDayStatesQuery(
    int? DeviceId = null,
    string? Status = null,
    int Page = 1,
    int PageSize = 50
) : IRequest<ErrorOr<FiscalDayStateListResult>>;

public sealed record FiscalDayStateListResult(
    List<FiscalDayStateDto> Days,
    int TotalCount,
    int OutstandingCount,
    int NeedsAttentionCount,
    int Page,
    int PageSize,
    bool HasMore);

/// <summary>
/// One device's one fiscal day.
/// </summary>
/// <remarks>
/// <c>OpenedAtLocal</c> is the taxpayer's wall clock, carrying no offset. Rendered as it is stored —
/// converting it moves the day boundary the receipts inside it were signed against.
///
/// <c>IngestedReceiptCount</c> is the receipts this application handed to the platform for this day.
/// Our count, not ZIMRA's: a mismatch against the offline file's own count is how a receipt that was
/// signed and never arrived is found, and a closed day can no longer accept one.
///
/// <c>IsComplete</c> means ZIMRA has this day's receipts. Anything short of it is still owed.
/// </remarks>
public sealed record FiscalDayStateDto(
    int Id,
    int DeviceId,
    int FiscalDayNo,
    string Status,
    DateTime? OpenedAtLocal,
    int? MaxDurationHours,
    DateTime? ClosedAtUtc,
    DateTime? FileGeneratedAtUtc,
    DateTime? FileSubmittedAtUtc,
    string? OfflineFileReference,
    int IngestedReceiptCount,
    int Attempts,
    string? LastError,
    bool DurationWarningRaised,
    bool IsComplete,
    bool NeedsAttention,
    DateTime UpdatedAt);
