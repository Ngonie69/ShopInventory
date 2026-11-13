namespace ShopInventory.DTOs;

public sealed class ApprovalStageDefinitionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ApprovalsRequired { get; set; } = 1;
    public int RejectionsRequired { get; set; } = 1;
    public List<Guid> AuthorizerUserIds { get; set; } = [];
    public List<string> AuthorizerRoles { get; set; } = [];

    /// <summary>Any, Source or Destination: which end of the transfer an authorizer must be assigned to.</summary>
    public string WarehouseScope { get; set; } = "Any";

    /// <summary>With a scoped stage, whether every warehouse on that end needs its own approvals.</summary>
    public bool ApprovalPerWarehouse { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class ApprovalTemplateDefinitionDto
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

public sealed class ApprovalStageProgressDto
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

    /// <summary>Any, Source or Destination, copied from the stage when the approval opened.</summary>
    public string WarehouseScope { get; set; } = "Any";

    public bool ApprovalPerWarehouse { get; set; }

    /// <summary>
    /// For a scoped stage, the document's warehouses on that end — the ones an authorizer must be
    /// assigned to. Empty on an unscoped stage.
    /// </summary>
    public List<string> ScopeWarehouses { get; set; } = [];

    /// <summary>
    /// For a stage that needs approvals per warehouse, where each warehouse stands. Empty otherwise.
    /// </summary>
    public List<ApprovalWarehouseProgressDto> WarehouseProgress { get; set; } = [];

    public List<ApprovalDecisionDto> Decisions { get; set; } = [];
}

public sealed class ApprovalWarehouseProgressDto
{
    public string WarehouseCode { get; set; } = string.Empty;
    public int ApprovalCount { get; set; }
    public int ApprovalsRequired { get; set; }

    /// <summary>Pending or Approved.</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// Approved because the person who raised the request runs this warehouse: asking for stock to come
    /// to your own warehouse is agreeing to take it. No decision is recorded for it.
    /// </summary>
    public bool ConsentedByRequester { get; set; }
}

public sealed class ApprovalDecisionDto
{
    public Guid StageId { get; set; }
    public Guid AuthorizerUserId { get; set; }
    public string AuthorizerName { get; set; } = string.Empty;
    public string AuthorizerRole { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string? Remarks { get; set; }

    /// <summary>For a scoped stage, the warehouses this decision speaks for.</summary>
    public List<string> WarehouseCodes { get; set; } = [];

    public DateTime DecidedAtUtc { get; set; }
}

public sealed class SubmitApprovalDecisionDto
{
    public Guid? StageId { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public bool GenerateDocument { get; set; }
}

public sealed class ApprovalDecisionOutcomeDto
{
    public Guid ApprovalRequestId { get; set; }
    public Guid StageId { get; set; }
    public string StageName { get; set; } = string.Empty;
    public string RequestStatus { get; set; } = string.Empty;
    public bool ApprovalProcessComplete { get; set; }
    public bool Rejected { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Where a document with these warehouses would go for approval, worked out before it is raised so
/// the person raising it can see who will be asked.
/// </summary>
public sealed class ApprovalRoutePreviewDto
{
    /// <summary>False when no active template matches, in which case the document cannot be raised.</summary>
    public bool Matched { get; set; }

    public Guid? TemplateId { get; set; }
    public string? TemplateName { get; set; }

    /// <summary>
    /// True when the raiser is an administrator: every stage is met as they raise it, so the document
    /// waits on nobody and a direct transfer posts straight to SAP.
    /// </summary>
    public bool ApprovedOnRaising { get; set; }

    public List<ApprovalRoutePreviewStageDto> Stages { get; set; } = [];
}

public sealed class ApprovalRoutePreviewStageDto
{
    public Guid StageId { get; set; }
    public string StageName { get; set; } = string.Empty;
    public int ApprovalsRequired { get; set; }
    public List<string> AuthorizerRoles { get; set; } = [];
    public string WarehouseScope { get; set; } = "Any";
    public bool ApprovalPerWarehouse { get; set; }
    public List<string> ScopeWarehouses { get; set; } = [];

    /// <summary>
    /// The scope warehouses no active authorizer is assigned to. Only an administrator can decide for
    /// those, which the person raising the document should know before they submit it.
    /// </summary>
    public List<string> WarehousesWithoutAuthorizer { get; set; } = [];

    /// <summary>Active users, administrators aside, who could decide this stage.</summary>
    public int AuthorizerCount { get; set; }
}
