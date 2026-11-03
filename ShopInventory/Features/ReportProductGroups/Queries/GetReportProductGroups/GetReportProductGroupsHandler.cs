using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.ReportProductGroups.Queries.GetReportProductGroups;

public sealed class GetReportProductGroupsHandler(ApplicationDbContext db)
    : IRequestHandler<GetReportProductGroupsQuery, ErrorOr<List<ReportProductGroupResult>>>
{
    public async Task<ErrorOr<List<ReportProductGroupResult>>> Handle(
        GetReportProductGroupsQuery request, CancellationToken cancellationToken)
    {
        var groups = db.ReportProductGroups.AsNoTracking();
        if (!request.IncludeInactive)
        {
            groups = groups.Where(g => g.IsActive);
        }

        var rows = await groups
            .OrderBy(g => g.SortOrder)
            .ThenBy(g => g.Name)
            .Select(g => new
            {
                g.Id,
                g.Name,
                g.Description,
                g.SortOrder,
                g.IsActive,
                g.CreatedAt,
                g.UpdatedAt,
                g.UpdatedBy,
                Members = g.Members.Select(m => new { m.Kind, m.Code, m.Name }).ToList()
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(g => new ReportProductGroupResult(
                g.Id, g.Name, g.Description, g.SortOrder, g.IsActive, g.CreatedAt, g.UpdatedAt, g.UpdatedBy,
                g.Members
                    .OrderBy(m => m.Kind)
                    .ThenBy(m => m.Code, StringComparer.Ordinal)
                    .Select(m => new ReportProductGroupMemberResult(m.Kind, m.Code, m.Name))
                    .ToList()))
            .ToList();
    }
}
