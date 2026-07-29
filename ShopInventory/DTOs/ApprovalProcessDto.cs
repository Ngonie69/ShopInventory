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
    public string? FromWarehouse { get; set; }
    public string? ToWarehouse { get; set; }
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
    public List<Guid> AuthorizerUserIds { get; set; } = [];
    public List<string> AuthorizerRoles { get; set; } = [];
    public List<ApprovalDecisionDto> Decisions { get; set; } = [];
}

public sealed class ApprovalDecisionDto
{
    public Guid StageId { get; set; }
    public Guid AuthorizerUserId { get; set; }
    public string AuthorizerName { get; set; } = string.Empty;
    public string AuthorizerRole { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string? Remarks { get; set; }
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
