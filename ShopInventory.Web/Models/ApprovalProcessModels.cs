namespace ShopInventory.Web.Models;

/// <summary>Which end of a transfer a stage's authorizers must be assigned to. Mirrors the API's values.</summary>
public static class ApprovalWarehouseScopes
{
    public const string Any = "Any";
    public const string Source = "Source";
    public const string Destination = "Destination";
}

public sealed class ApprovalStageDefinitionModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ApprovalsRequired { get; set; } = 1;
    public int RejectionsRequired { get; set; } = 1;
    public List<Guid> AuthorizerUserIds { get; set; } = [];
    public List<string> AuthorizerRoles { get; set; } = [];

    /// <summary>Any, Source or Destination.</summary>
    public string WarehouseScope { get; set; } = ApprovalWarehouseScopes.Any;

    /// <summary>With a scoped stage, whether every warehouse on that end needs its own approvals.</summary>
    public bool ApprovalPerWarehouse { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class ApprovalTemplateDefinitionModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string DocumentType { get; set; } = "InventoryTransferRequest";
    public List<Guid> OriginatorUserIds { get; set; } = [];
    public List<string> OriginatorRoles { get; set; } = [];
    public List<Guid> StageIds { get; set; } = [];

    /// <summary>Every line's source must be one of these; empty matches any source.</summary>
    public List<string> FromWarehouses { get; set; } = [];

    /// <summary>Every line's destination must be one of these; empty matches any destination.</summary>
    public List<string> ToWarehouses { get; set; } = [];

    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ApprovalStageProgressModel
{
    public Guid StageId { get; set; }
    public string StageName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ApprovalsRequired { get; set; }
    public int RejectionsRequired { get; set; }
    public int ApprovalCount { get; set; }
    public int RejectionCount { get; set; }
    public string Status { get; set; } = "Pending";

    /// <summary>Met by an administrator raising the document, or changing it, rather than by a decision.</summary>
    public bool ApprovedByAdministrator { get; set; }

    public List<Guid> AuthorizerUserIds { get; set; } = [];
    public List<string> AuthorizerRoles { get; set; } = [];
    public string WarehouseScope { get; set; } = ApprovalWarehouseScopes.Any;
    public bool ApprovalPerWarehouse { get; set; }

    /// <summary>For a scoped stage, the document's warehouses an authorizer must be assigned to.</summary>
    public List<string> ScopeWarehouses { get; set; } = [];

    /// <summary>For a per-warehouse stage, where each warehouse stands.</summary>
    public List<ApprovalWarehouseProgressModel> WarehouseProgress { get; set; } = [];

    public List<ApprovalDecisionModel> Decisions { get; set; } = [];
}

public sealed class ApprovalWarehouseProgressModel
{
    public string WarehouseCode { get; set; } = string.Empty;
    public int ApprovalCount { get; set; }
    public int ApprovalsRequired { get; set; }
    public string Status { get; set; } = "Pending";

    /// <summary>Approved because whoever raised the request runs this warehouse; nobody decided it.</summary>
    public bool ConsentedByRequester { get; set; }
}

public sealed class ApprovalDecisionModel
{
    public Guid StageId { get; set; }
    public Guid AuthorizerUserId { get; set; }
    public string AuthorizerName { get; set; } = string.Empty;
    public string AuthorizerRole { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public List<string> WarehouseCodes { get; set; } = [];
    public DateTime DecidedAtUtc { get; set; }
}

public sealed class SubmitApprovalDecisionModel
{
    public Guid? StageId { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public bool GenerateDocument { get; set; }
}

/// <summary>Who a direct transfer with given warehouses would wait on. Mirrors ApprovalRoutePreviewDto.</summary>
public sealed class ApprovalRoutePreviewModel
{
    public bool Matched { get; set; }
    public Guid? TemplateId { get; set; }
    public string? TemplateName { get; set; }

    /// <summary>The raiser is an administrator: every stage is met as they submit, so it posts at once.</summary>
    public bool ApprovedOnRaising { get; set; }

    public List<ApprovalRoutePreviewStageModel> Stages { get; set; } = [];
}

public sealed class ApprovalRoutePreviewStageModel
{
    public Guid StageId { get; set; }
    public string StageName { get; set; } = string.Empty;
    public int ApprovalsRequired { get; set; }
    public List<string> AuthorizerRoles { get; set; } = [];
    public string WarehouseScope { get; set; } = ApprovalWarehouseScopes.Any;
    public bool ApprovalPerWarehouse { get; set; }
    public List<string> ScopeWarehouses { get; set; } = [];
    public List<string> WarehousesWithoutAuthorizer { get; set; } = [];
    public int AuthorizerCount { get; set; }
}
