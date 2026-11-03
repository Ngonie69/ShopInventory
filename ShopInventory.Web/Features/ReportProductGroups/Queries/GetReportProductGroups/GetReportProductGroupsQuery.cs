using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.ReportProductGroups.Queries.GetReportProductGroups;

public sealed record GetReportProductGroupsQuery(bool IncludeInactive = true) : IRequest<ErrorOr<List<ReportProductGroupDto>>>;
