using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesReview;

namespace ShopInventory.Web.Features.Reports.Commands.DesktopSalesReviewEmail;

/// <summary>Emails the review now: the last complete week or month, or the period given.</summary>
public sealed record SendDesktopSalesReviewEmailCommand(string Cadence, DateTime? FromDate, DateTime? ToDate, List<string> Recipients)
    : IRequest<ErrorOr<DesktopSalesReviewEmailResult>>;
