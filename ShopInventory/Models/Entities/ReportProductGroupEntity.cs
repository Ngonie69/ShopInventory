using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// A named set of products a report follows on its own, such as "Complimentary lines" or a new ice cream
/// range. Administrators keep the membership up to date as products come and go.
/// </summary>
/// <remarks>
/// Membership is current, not dated: a report applies today's members to every period it compares, so two
/// months are measured on the same products.
/// </remarks>
[Table("ReportProductGroups")]
public class ReportProductGroupEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>Order the groups appear in on the page and in the workbook.</summary>
    public int SortOrder { get; set; }

    /// <summary>A retired group stays on file but is left out of reports.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    [MaxLength(100)]
    public string? UpdatedBy { get; set; }

    public List<ReportProductGroupMemberEntity> Members { get; set; } = new();
}

/// <summary>What a member of a report product group names.</summary>
public enum ReportProductGroupMemberKind
{
    /// <summary>One item, by item code.</summary>
    Item = 0,

    /// <summary>Every item in an SAP item group, by group number, so new items join without an edit.</summary>
    ItemGroup = 1
}

[Table("ReportProductGroupMembers")]
public class ReportProductGroupMemberEntity
{
    [Key]
    public int Id { get; set; }

    public int GroupId { get; set; }

    public ReportProductGroupEntity Group { get; set; } = null!;

    public ReportProductGroupMemberKind Kind { get; set; }

    /// <summary>An item code (upper-cased) or an SAP item group number, as text.</summary>
    [MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    /// <summary>The item or item group name when it was added, for display only.</summary>
    [MaxLength(200)]
    public string? Name { get; set; }
}
