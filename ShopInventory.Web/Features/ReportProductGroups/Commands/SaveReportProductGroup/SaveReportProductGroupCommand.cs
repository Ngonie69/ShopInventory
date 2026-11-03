using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.ReportProductGroups.Commands.SaveReportProductGroup;

/// <summary>Creates a product group (Id null) or replaces one, members and all.</summary>
public sealed record SaveReportProductGroupCommand(
    int? Id,
    string Name,
    string? Description,
    int SortOrder,
    bool IsActive,
    IReadOnlyList<ReportProductGroupMemberDto> Members) : IRequest<ErrorOr<ReportProductGroupDto>>;
