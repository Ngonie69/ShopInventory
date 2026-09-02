using System.Text.Json.Serialization;

namespace ShopInventory.Models;

/// <summary>
/// A SAP Business One approval request (OWDD) as the Service Layer's <c>ApprovalRequests</c> entity set
/// returns it. For a document raised in the B1 client and held by an approval procedure,
/// <see cref="DraftEntry"/> names the draft (ODRF) that becomes the real document once the request is
/// approved and the draft is added.
/// </summary>
public class SAPApprovalRequest
{
    [JsonPropertyName("Code")]
    public int Code { get; set; }

    [JsonPropertyName("ApprovalTemplatesID")]
    public int? ApprovalTemplatesID { get; set; }

    /// <summary>The B1 object type as a string — "14" for an A/R credit memo.</summary>
    [JsonPropertyName("ObjectType")]
    public string? ObjectType { get; set; }

    [JsonPropertyName("IsDraft")]
    public string? IsDraft { get; set; }

    /// <summary>The generated document's DocEntry once the request is generated; null while it is held.</summary>
    [JsonPropertyName("ObjectEntry")]
    public int? ObjectEntry { get; set; }

    /// <summary>One of <see cref="SapApprovalRequestStatuses"/>.</summary>
    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("Remarks")]
    public string? Remarks { get; set; }

    /// <summary>The <c>ApprovalStages</c> code the request is waiting on.</summary>
    [JsonPropertyName("CurrentStage")]
    public int? CurrentStage { get; set; }

    /// <summary>The SAP user (<c>Users.InternalKey</c>) who raised the document.</summary>
    [JsonPropertyName("OriginatorID")]
    public int? OriginatorID { get; set; }

    [JsonPropertyName("CreationDate")]
    public string? CreationDate { get; set; }

    [JsonPropertyName("CreationTime")]
    public string? CreationTime { get; set; }

    /// <summary>The <c>Drafts</c> DocEntry the request holds.</summary>
    [JsonPropertyName("DraftEntry")]
    public int? DraftEntry { get; set; }

    [JsonPropertyName("DraftType")]
    public string? DraftType { get; set; }

    [JsonPropertyName("ApprovalRequestLines")]
    public List<SAPApprovalRequestLine>? ApprovalRequestLines { get; set; }
}

/// <summary>
/// One approver's row on a request (WDD1): who may decide at which stage, and what they decided.
/// </summary>
public class SAPApprovalRequestLine
{
    [JsonPropertyName("StageCode")]
    public int? StageCode { get; set; }

    /// <summary>The approver's <c>Users.InternalKey</c>.</summary>
    [JsonPropertyName("UserID")]
    public int? UserID { get; set; }

    /// <summary>One of <see cref="SapApprovalDecisions"/>.</summary>
    [JsonPropertyName("Status")]
    public string? Status { get; set; }

    [JsonPropertyName("Remarks")]
    public string? Remarks { get; set; }

    [JsonPropertyName("UpdateDate")]
    public string? UpdateDate { get; set; }

    [JsonPropertyName("UpdateTime")]
    public string? UpdateTime { get; set; }

    [JsonPropertyName("CreationDate")]
    public string? CreationDate { get; set; }

    [JsonPropertyName("CreationTime")]
    public string? CreationTime { get; set; }
}

/// <summary>An approval template (OWTM) header, as much of it as the approval pages show.</summary>
public class SAPApprovalTemplate
{
    [JsonPropertyName("Code")]
    public int Code { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    /// <summary>tYES or tNO.</summary>
    [JsonPropertyName("IsActive")]
    public string? IsActive { get; set; }
}

/// <summary>An approval stage (OWST) and the users allowed to decide it.</summary>
public class SAPApprovalStage
{
    [JsonPropertyName("Code")]
    public int Code { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("NoOfApproversRequired")]
    public int? NoOfApproversRequired { get; set; }

    [JsonPropertyName("ApprovalStageApprovers")]
    public List<SAPApprovalStageApprover>? ApprovalStageApprovers { get; set; }
}

public class SAPApprovalStageApprover
{
    /// <summary>The approver's <c>Users.InternalKey</c>.</summary>
    [JsonPropertyName("UserID")]
    public int? UserID { get; set; }
}

/// <summary>A SAP user (OUSR) — the key the approval collections refer to people by.</summary>
public class SAPUser
{
    [JsonPropertyName("InternalKey")]
    public int InternalKey { get; set; }

    [JsonPropertyName("UserCode")]
    public string? UserCode { get; set; }

    [JsonPropertyName("UserName")]
    public string? UserName { get; set; }
}
