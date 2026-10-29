using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>The Reports Hub's activity cards for a period.</summary>
public sealed record GetReportsActivityQuery(ReportsPeriod Period) : IRequest<ErrorOr<ReportsActivityResult>>;
