namespace ShopInventory.Web.Features.ReportProductGroups;

/// <summary>What a member of a product group names. Mirrors the API's <c>ReportProductGroupMemberKind</c>.</summary>
public enum ReportProductGroupMemberKind
{
    Item = 0,
    ItemGroup = 1
}

/// <summary>Mirrors the API's <c>ReportProductGroupResult</c>.</summary>
public sealed record ReportProductGroupDto(
    int Id,
    string Name,
    string? Description,
    int SortOrder,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string? UpdatedBy,
    List<ReportProductGroupMemberDto> Members);

public sealed record ReportProductGroupMemberDto(ReportProductGroupMemberKind Kind, string Code, string? Name);
