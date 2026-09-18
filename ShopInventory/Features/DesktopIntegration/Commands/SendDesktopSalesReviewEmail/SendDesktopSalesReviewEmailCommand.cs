using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.SendDesktopSalesReviewEmail;

/// <summary>
/// Emails the business review — the findings in the message, the full review as a PDF attached.
/// </summary>
/// <param name="Cadence">weekly or monthly for the last complete period; custom for <paramref name="FromDate"/> to <paramref name="ToDate"/>.</param>
/// <param name="Scheduled">
/// True from the job: the schedule's switches, recipients and read-as account apply, and a period already
/// sent is not sent again. False from a person: their own scope, the recipients they name, sent now.
/// </param>
/// <param name="CallerUserId">Whose scope the review is read under; null from the job, which reads as the schedule's admin.</param>
/// <param name="CallerName">Who sent it, for the audit trail.</param>
/// <param name="FromDate">First day of a custom period.</param>
/// <param name="ToDate">Last day of a custom period; defaults to its first.</param>
/// <param name="Recipients">Who to send to; empty means the schedule's list.</param>
public sealed record SendDesktopSalesReviewEmailCommand(
    string Cadence,
    bool Scheduled,
    Guid? CallerUserId = null,
    string? CallerName = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    List<string>? Recipients = null
) : IRequest<ErrorOr<DesktopSalesReviewEmailResult>>;
