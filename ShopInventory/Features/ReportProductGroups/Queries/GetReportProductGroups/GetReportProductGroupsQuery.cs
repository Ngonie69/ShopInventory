using ErrorOr;
using MediatR;

namespace ShopInventory.Features.ReportProductGroups.Queries.GetReportProductGroups;

public sealed record GetReportProductGroupsQuery(bool IncludeInactive) : IRequest<ErrorOr<List<ReportProductGroupResult>>>;
