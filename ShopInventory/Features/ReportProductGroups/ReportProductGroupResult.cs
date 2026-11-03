using ShopInventory.Models.Entities;

namespace ShopInventory.Features.ReportProductGroups;

public sealed record ReportProductGroupResult(
    int Id,
    string Name,
    string? Description,
    int SortOrder,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string? UpdatedBy,
    IReadOnlyList<ReportProductGroupMemberResult> Members);

public sealed record ReportProductGroupMemberResult(ReportProductGroupMemberKind Kind, string Code, string? Name);
