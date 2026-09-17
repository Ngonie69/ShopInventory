using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;

/// <summary>The POD upload status report for a range of invoice dates.</summary>
/// <param name="FromDate">The first invoice date in the report.</param>
/// <param name="ToDate">The last invoice date in the report.</param>
/// <param name="UserId">
/// The account asking. A <c>Driver</c> is scoped to the shops assigned to them; anyone else sees every
/// invoice unless <paramref name="CustomerCodeScope"/> says otherwise.
/// </param>
/// <param name="IncludeCreditNoteActivity">
/// Also pull in invoices from outside the range that have credit-note activity inside it. Such a report
/// is never cached.
/// </param>
/// <param name="CustomerCodeScope">
/// Shops to scope the report to, whoever is asking. Wins over the caller's own role. An empty scope is an
/// empty report, not an unscoped one: a list with no shops on it has no invoices to show. The van sales
/// delivery list passes the drivers' shop list here, because a van rep is not a <c>Driver</c> and would
/// otherwise be handed every invoice in the company. The POD warm job passes a scoped shape's shops
/// here too, because its cache key is a hash of them and a rebuild without them would rebuild the
/// global report instead.
/// </param>
/// <param name="IsWarmRebuild">
/// Set by <c>PodReportWarmJob</c> only. A warm rebuild reads and saves the cache like any other request,
/// but nobody asked for the report, so it does not renew the shape in <c>PodReportWarmSet</c>. If it did,
/// a shape asked for once would be rebuilt against SAP forever.
/// </param>
public sealed record GetPodUploadStatusQuery(
    DateTime FromDate,
    DateTime ToDate,
    Guid? UserId,
    bool IncludeCreditNoteActivity = false,
    IReadOnlyCollection<string>? CustomerCodeScope = null,
    bool IsWarmRebuild = false) : IRequest<ErrorOr<PodUploadStatusReportDto>>;
