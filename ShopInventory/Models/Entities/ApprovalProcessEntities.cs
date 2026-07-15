using System.ComponentModel.DataAnnotations;

namespace ShopInventory.Models.Entities;

public sealed class ApprovalStageDefinitionEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ApprovalsRequired { get; set; } = 1;
    public int RejectionsRequired { get; set; } = 1;
    public string AuthorizerUserIdsJson { get; set; } = "[]";
    public string AuthorizerRolesJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public sealed class ApprovalTemplateDefinitionEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DocumentType { get; set; } = ApprovalDocumentTypes.InventoryTransferRequest;
    public string OriginatorUserIdsJson { get; set; } = "[]";
    public string OriginatorRolesJson { get; set; } = "[]";
    public string StageIdsJson { get; set; } = "[]";
    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
}

public sealed class ApprovalRequestEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApprovalTemplateId { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public string DocumentType { get; set; } = ApprovalDocumentTypes.InventoryTransferRequest;
    public string DocumentKey { get; set; } = string.Empty;
    public string? DocumentNumber { get; set; }
    public Guid? OriginatorUserId { get; set; }
    public string OriginatorName { get; set; } = string.Empty;
    public string OriginatorRole { get; set; } = string.Empty;
    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }
    public string StageSnapshotsJson { get; set; } = "[]";
    public string Status { get; set; } = ApprovalRequestStatuses.Pending;
    public int? GeneratedDocumentEntry { get; set; }
    public int? GeneratedDocumentNumber { get; set; }
    public Guid? GeneratedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? GeneratedAtUtc { get; set; }
    public ICollection<ApprovalDecisionEntity> Decisions { get; set; } = new List<ApprovalDecisionEntity>();
}

public sealed class ApprovalDecisionEntity
{
    [Key]
    public long Id { get; set; }
    public Guid ApprovalRequestId { get; set; }
    public ApprovalRequestEntity ApprovalRequest { get; set; } = null!;
    public Guid StageId { get; set; }
    public string StageName { get; set; } = string.Empty;
    public Guid AuthorizerUserId { get; set; }
    public string AuthorizerName { get; set; } = string.Empty;
    public string AuthorizerRole { get; set; } = string.Empty;
    public string Decision { get; set; } = ApprovalDecisionValues.Pending;
    public string? Remarks { get; set; }
    public DateTime DecidedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class ApprovalDocumentTypes
{
    public const string InventoryTransferRequest = "InventoryTransferRequest";
}

public static class ApprovalRequestStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string NotApproved = "NotApproved";
    public const string Generated = "Generated";
    public const string GeneratedByAuthorizer = "GeneratedByAuthorizer";
    public const string Cancelled = "Cancelled";
}

public static class ApprovalDecisionValues
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string NotApproved = "NotApproved";
}
