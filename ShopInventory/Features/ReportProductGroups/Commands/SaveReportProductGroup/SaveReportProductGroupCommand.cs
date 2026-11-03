using ErrorOr;
using MediatR;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.ReportProductGroups.Commands.SaveReportProductGroup;

/// <summary>Creates a group (Id null) or replaces one: its details and its whole member list.</summary>
public sealed record SaveReportProductGroupCommand(
    int? Id,
    string Name,
    string? Description,
    int SortOrder,
    bool IsActive,
    IReadOnlyList<SaveReportProductGroupMember> Members,
    string? UpdatedBy) : IRequest<ErrorOr<ReportProductGroupResult>>;

public sealed record SaveReportProductGroupMember(ReportProductGroupMemberKind Kind, string Code, string? Name);
