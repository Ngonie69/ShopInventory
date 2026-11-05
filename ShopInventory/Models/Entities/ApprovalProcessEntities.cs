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

    /// <summary>
    /// Which end of the transfer an authorizer must be assigned to: <see cref="ApprovalWarehouseScopes.Any"/>,
    /// the source, or the destination. A scoped stage only accepts authorizers whose account names one
    /// of the document's warehouses on that end.
    /// </summary>
    public string WarehouseScope { get; set; } = ApprovalWarehouseScopes.Any;

    /// <summary>
    /// With a scoped stage, whether each warehouse on that end needs its own approvals. A return to
    /// two dispatch warehouses then needs a stock officer from each, rather than from either.
    /// </summary>
    public bool ApprovalPerWarehouse { get; set; }

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

    /// <summary>
    /// JSON array of warehouse codes. The template holds a document only when every line's source is
    /// one of them; an empty list matches any source.
    /// </summary>
    public string FromWarehousesJson { get; set; } = "[]";

    /// <summary>JSON array of warehouse codes, matched against every line's destination.</summary>
    public string ToWarehousesJson { get; set; } = "[]";

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

    /// <summary>
    /// Who last sent an approved document back for approval by changing it, and when. That person
    /// may not approve the change they made, just as the originator may not approve the document.
    /// Null until the document is first reopened.
    /// </summary>
    public Guid? ReopenedByUserId { get; set; }
    public DateTime? ReopenedAtUtc { get; set; }

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

    /// <summary>
    /// For a warehouse-scoped stage, the document's warehouses this decision speaks for: those the
    /// authorizer was assigned to when they decided, or all of them for an administrator. Kept on the
    /// decision so a later change to someone's assignments does not rewrite an approval already given.
    /// Null on an unscoped stage.
    /// </summary>
    public string? WarehouseCodesJson { get; set; }

    public DateTime DecidedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class ApprovalWarehouseScopes
{
    /// <summary>Any authorizer holding the stage's role or name may decide.</summary>
    public const string Any = "Any";

    /// <summary>The authorizer must be assigned to a warehouse the stock leaves from.</summary>
    public const string Source = "Source";

    /// <summary>The authorizer must be assigned to a warehouse the stock is going to.</summary>
    public const string Destination = "Destination";

    public static readonly IReadOnlyList<string> All = [Any, Source, Destination];

    public static string Normalize(string? scope) =>
        All.FirstOrDefault(item => string.Equals(item, scope?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Any;
}

public static class ApprovalDocumentTypes
{
    public const string InventoryTransferRequest = "InventoryTransferRequest";

    /// <summary>A direct inventory transfer, held locally until fully approved.</summary>
    public const string InventoryTransfer = "InventoryTransfer";

    /// <summary>
    /// A change to an existing SAP transfer request, held locally until approved. Raised when
    /// the editor is scoped to warehouses that do not include the request's source.
    /// </summary>
    public const string InventoryTransferRequestEdit = "InventoryTransferRequestEdit";

    public static readonly IReadOnlyList<string> All =
        [InventoryTransferRequest, InventoryTransfer, InventoryTransferRequestEdit];

    public static bool IsKnown(string? documentType) =>
        All.Contains(documentType, StringComparer.OrdinalIgnoreCase);
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
