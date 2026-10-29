using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>The Reports Hub's headline figures for a period, with the period before it to compare.</summary>
public sealed record GetReportsHeadlineQuery(ReportsPeriod Period) : IRequest<ErrorOr<ReportsHeadlineResult>>;
