using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;

/// <summary>The POD upload status report for a range of invoice dates.</summary>
/// <param name="UserId">
/// The account asking. A <c>Driver</c> is scoped to the shops assigned to them; anyone else sees every
/// invoice unless <paramref name="CustomerCodeScope"/> says otherwise.
/// </param>
/// <param name="CustomerCodeScope">
/// Shops to scope the report to, whoever is asking. Wins over the caller's own role. An empty scope is an
/// empty report, not an unscoped one: a list with no shops on it has no invoices to show. The van sales
/// delivery list passes the drivers' shop list here, because a van rep is not a <c>Driver</c> and would
/// otherwise be handed every invoice in the company.
/// </param>
public sealed record GetPodUploadStatusQuery(
    DateTime FromDate,
    DateTime ToDate,
    Guid? UserId,
    bool IncludeCreditNoteActivity = false,
    IReadOnlyCollection<string>? CustomerCodeScope = null) : IRequest<ErrorOr<PodUploadStatusReportDto>>;
