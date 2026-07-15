using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

public interface IInventoryTransferApprovalService
{
    Task<ApprovalRequestEntity> EnsureRequestAsync(InventoryTransferRequest document, Guid? originatorUserId, CancellationToken cancellationToken);
    Task EnrichAsync(IEnumerable<InventoryTransferRequestDto> documents, CancellationToken cancellationToken);
    Task<ErrorOr<ApprovalDecisionOutcomeDto>> SubmitDecisionAsync(
        InventoryTransferRequest document,
        Guid authorizerUserId,
        string decision,
        Guid? stageId,
        string? remarks,
        CancellationToken cancellationToken);
    Task MarkGeneratedAsync(int requestDocEntry, int transferDocEntry, int transferDocNum, Guid generatedByUserId, bool byAuthorizer, CancellationToken cancellationToken);
    Task<List<ApprovalStageDefinitionDto>> GetStagesAsync(CancellationToken cancellationToken);
    Task<ErrorOr<ApprovalStageDefinitionDto>> SaveStageAsync(ApprovalStageDefinitionDto stage, CancellationToken cancellationToken);
    Task<ErrorOr<Deleted>> DeleteStageAsync(Guid id, CancellationToken cancellationToken);
    Task<List<ApprovalTemplateDefinitionDto>> GetTemplatesAsync(CancellationToken cancellationToken);
    Task<ErrorOr<ApprovalTemplateDefinitionDto>> SaveTemplateAsync(ApprovalTemplateDefinitionDto template, CancellationToken cancellationToken);
    Task<ErrorOr<Deleted>> DeleteTemplateAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class InventoryTransferApprovalService(
    ApplicationDbContext context,
    INotificationService notificationService,
    ILogger<InventoryTransferApprovalService> logger)
    : IInventoryTransferApprovalService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid DepotStageId = Guid.Parse("11000000-0000-0000-0000-000000000001");
    private static readonly Guid StockStageId = Guid.Parse("11000000-0000-0000-0000-000000000002");
    private static readonly Guid AdminStageId = Guid.Parse("11000000-0000-0000-0000-000000000003");
    private static readonly Guid DispatchTemplateId = Guid.Parse("22000000-0000-0000-0000-000000000001");
    private static readonly Guid DepotTemplateId = Guid.Parse("22000000-0000-0000-0000-000000000002");
    private static readonly Guid FallbackTemplateId = Guid.Parse("22000000-0000-0000-0000-000000000003");

    public async Task<ApprovalRequestEntity> EnsureRequestAsync(
        InventoryTransferRequest document,
        Guid? originatorUserId,
        CancellationToken cancellationToken)
    {
        await EnsureDefaultsAsync(cancellationToken);
        var documentKey = document.DocEntry.ToString();
        var existing = await context.ApprovalRequests
            .Include(request => request.Decisions)
            .FirstOrDefaultAsync(request =>
                request.DocumentType == ApprovalDocumentTypes.InventoryTransferRequest &&
                request.DocumentKey == documentKey,
                cancellationToken);
        if (existing is not null)
            return existing;

        var originator = await ResolveOriginatorAsync(document, originatorUserId, cancellationToken);
        var template = await SelectTemplateAsync(originator, document, cancellationToken)
            ?? throw new InvalidOperationException("No active approval template is available for inventory transfer requests.");
        var stageIds = Deserialize<Guid>(template.StageIdsJson);
        var stages = await context.ApprovalStageDefinitions
            .AsNoTracking()
            .Where(stage => stage.IsActive && stageIds.Contains(stage.Id))
            .ToListAsync(cancellationToken);
        var orderedStages = stageIds
            .Select(stageId => stages.FirstOrDefault(stage => stage.Id == stageId))
            .Where(stage => stage is not null)
            .Select(stage => ToSnapshot(stage!))
            .ToList();
        if (orderedStages.Count == 0)
            throw new InvalidOperationException($"Approval template '{template.Name}' has no active approval stages.");

        var request = new ApprovalRequestEntity
        {
            ApprovalTemplateId = template.Id,
            TemplateName = template.Name,
            DocumentType = ApprovalDocumentTypes.InventoryTransferRequest,
            DocumentKey = documentKey,
            DocumentNumber = document.DocNum.ToString(),
            OriginatorUserId = originator?.Id,
            OriginatorName = originator?.Username ?? ExtractCommentValue(document.Comments, "Requester") ?? "Unknown",
            OriginatorRole = originator?.Role ?? "Unknown",
            FromWarehouse = document.FromWarehouse,
            ToWarehouse = document.ToWarehouse,
            StageSnapshotsJson = Serialize(orderedStages),
            Status = IsClosed(document.DocumentStatus)
                ? ApprovalRequestStatuses.Generated
                : ApprovalRequestStatuses.Pending
        };
        context.ApprovalRequests.Add(request);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await NotifyPendingAuthorizersAsync(request, orderedStages, cancellationToken);
            return request;
        }
        catch (DbUpdateException)
        {
            context.Entry(request).State = EntityState.Detached;
            return await context.ApprovalRequests
                .Include(item => item.Decisions)
                .SingleAsync(item =>
                    item.DocumentType == ApprovalDocumentTypes.InventoryTransferRequest &&
                    item.DocumentKey == documentKey,
                    cancellationToken);
        }
    }

    public async Task EnrichAsync(IEnumerable<InventoryTransferRequestDto> documents, CancellationToken cancellationToken)
    {
        foreach (var dto in documents)
        {
            dto.RequesterName ??= ExtractCommentValue(dto.Comments, "Requester");
            dto.RequesterEmail ??= ExtractCommentValue(dto.Comments, "Email");
            var model = new InventoryTransferRequest
            {
                DocEntry = dto.DocEntry,
                DocNum = dto.DocNum,
                DocumentStatus = dto.DocumentStatus,
                FromWarehouse = dto.FromWarehouse,
                ToWarehouse = dto.ToWarehouse,
                Comments = dto.Comments,
                RequesterEmail = dto.RequesterEmail,
                RequesterName = dto.RequesterName
            };
            var request = await EnsureRequestAsync(model, null, cancellationToken);
            ApplyProgress(dto, request);
        }
    }

    public async Task<ErrorOr<ApprovalDecisionOutcomeDto>> SubmitDecisionAsync(
        InventoryTransferRequest document,
        Guid authorizerUserId,
        string decision,
        Guid? stageId,
        string? remarks,
        CancellationToken cancellationToken)
    {
        if (decision is not ApprovalDecisionValues.Approved and not ApprovalDecisionValues.NotApproved)
            return Errors.InventoryTransfer.ValidationFailed("Decision must be Approved or NotApproved.");

        var authorizer = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == authorizerUserId && user.IsActive, cancellationToken);
        if (authorizer is null)
            return Errors.InventoryTransfer.ApproverNotAuthenticated;

        var request = await EnsureRequestAsync(document, null, cancellationToken);
        if (request.Status is ApprovalRequestStatuses.Generated or ApprovalRequestStatuses.GeneratedByAuthorizer or ApprovalRequestStatuses.Cancelled)
            return Errors.InventoryTransfer.ApprovalAlreadyDecided(request.Status);

        var snapshots = Deserialize<ApprovalStageSnapshot>(request.StageSnapshotsJson);
        var progress = BuildProgress(request, snapshots);
        var eligibleStages = progress
            .Where(stage => (stageId is null || stage.StageId == stageId) && IsAuthorizer(authorizer, stage))
            .ToList();
        if (eligibleStages.Count == 0)
            return Error.Forbidden("ApprovalProcess.NotAuthorizer", "You are not an authorizer for the selected approval stage.");

        var selectedStage = eligibleStages.FirstOrDefault(stage => stage.Status == ApprovalRequestStatuses.Pending)
            ?? eligibleStages[0];
        var existingDecision = request.Decisions.FirstOrDefault(item =>
            item.StageId == selectedStage.StageId && item.AuthorizerUserId == authorizer.Id);
        if (existingDecision is null)
        {
            existingDecision = new ApprovalDecisionEntity
            {
                ApprovalRequestId = request.Id,
                StageId = selectedStage.StageId,
                StageName = selectedStage.StageName,
                AuthorizerUserId = authorizer.Id,
                AuthorizerName = authorizer.Username,
                AuthorizerRole = authorizer.Role
            };
            request.Decisions.Add(existingDecision);
        }

        existingDecision.Decision = decision;
        existingDecision.Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
        existingDecision.DecidedAtUtc = DateTime.UtcNow;

        progress = BuildProgress(request, snapshots);
        request.Status = progress.Any(stage => stage.Status == ApprovalRequestStatuses.NotApproved)
            ? ApprovalRequestStatuses.NotApproved
            : progress.All(stage => stage.Status == ApprovalRequestStatuses.Approved)
                ? ApprovalRequestStatuses.Approved
                : ApprovalRequestStatuses.Pending;
        request.CompletedAtUtc = request.Status == ApprovalRequestStatuses.Pending ? null : DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        var notificationEntityId = ApprovalNotificationEntityId(request.DocumentKey, selectedStage.StageId);
        await context.Notifications
            .Where(item => item.Category == "TransferApproval" &&
                           item.EntityType == "TransferRequestApproval" &&
                           item.EntityId == notificationEntityId &&
                           item.TargetUsername == authorizer.Username &&
                           !item.IsRead)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.IsRead, true)
                .SetProperty(item => item.ReadAt, DateTime.UtcNow), cancellationToken);

        if (request.Status == ApprovalRequestStatuses.Pending)
            await NotifyPendingAuthorizersAsync(request, snapshots, cancellationToken);
        else
            await MarkApprovalNotificationsReadAsync(request.DocumentKey, cancellationToken);

        return new ApprovalDecisionOutcomeDto
        {
            ApprovalRequestId = request.Id,
            StageId = selectedStage.StageId,
            StageName = selectedStage.StageName,
            RequestStatus = request.Status,
            ApprovalProcessComplete = request.Status == ApprovalRequestStatuses.Approved,
            Rejected = request.Status == ApprovalRequestStatuses.NotApproved,
            Message = request.Status switch
            {
                ApprovalRequestStatuses.Approved => "All required approval stages are complete.",
                ApprovalRequestStatuses.NotApproved => "The approval request has been rejected.",
                _ => $"Decision recorded for {selectedStage.StageName}; other approvals are still pending."
            }
        };
    }

    public async Task MarkGeneratedAsync(
        int requestDocEntry,
        int transferDocEntry,
        int transferDocNum,
        Guid generatedByUserId,
        bool byAuthorizer,
        CancellationToken cancellationToken)
    {
        var request = await context.ApprovalRequests.FirstOrDefaultAsync(item =>
            item.DocumentType == ApprovalDocumentTypes.InventoryTransferRequest &&
            item.DocumentKey == requestDocEntry.ToString(), cancellationToken);
        if (request is null)
            return;
        request.Status = byAuthorizer ? ApprovalRequestStatuses.GeneratedByAuthorizer : ApprovalRequestStatuses.Generated;
        request.GeneratedDocumentEntry = transferDocEntry;
        request.GeneratedDocumentNumber = transferDocNum;
        request.GeneratedByUserId = generatedByUserId;
        request.GeneratedAtUtc = DateTime.UtcNow;
        request.CompletedAtUtc ??= DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<ApprovalStageDefinitionDto>> GetStagesAsync(CancellationToken cancellationToken)
    {
        await EnsureDefaultsAsync(cancellationToken);
        return (await context.ApprovalStageDefinitions.AsNoTracking().OrderBy(stage => stage.Name).ToListAsync(cancellationToken))
            .Select(ToDto).ToList();
    }

    public async Task<ErrorOr<ApprovalStageDefinitionDto>> SaveStageAsync(ApprovalStageDefinitionDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.ApprovalsRequired < 1 || dto.RejectionsRequired < 1)
            return Errors.InventoryTransfer.ValidationFailed("Stage name and positive approval/rejection thresholds are required.");
        if (dto.AuthorizerUserIds.Count == 0 && dto.AuthorizerRoles.Count == 0)
            return Errors.InventoryTransfer.ValidationFailed("At least one authorizer user or role is required.");

        var entity = dto.Id == Guid.Empty
            ? new ApprovalStageDefinitionEntity()
            : await context.ApprovalStageDefinitions.FirstOrDefaultAsync(stage => stage.Id == dto.Id, cancellationToken);
        if (entity is null)
            return Error.NotFound("ApprovalProcess.StageNotFound", "Approval stage was not found.");
        entity.Name = dto.Name.Trim();
        entity.Description = dto.Description?.Trim();
        entity.ApprovalsRequired = dto.ApprovalsRequired;
        entity.RejectionsRequired = dto.RejectionsRequired;
        entity.AuthorizerUserIdsJson = Serialize(dto.AuthorizerUserIds.Distinct());
        entity.AuthorizerRolesJson = Serialize(dto.AuthorizerRoles.Where(role => !string.IsNullOrWhiteSpace(role)).Select(role => role.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        entity.IsActive = dto.IsActive;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (dto.Id == Guid.Empty) context.ApprovalStageDefinitions.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task<ErrorOr<Deleted>> DeleteStageAsync(Guid id, CancellationToken cancellationToken)
    {
        var stage = await context.ApprovalStageDefinitions.FindAsync([id], cancellationToken);
        if (stage is null) return Result.Deleted;
        var templates = await context.ApprovalTemplateDefinitions.AsNoTracking().ToListAsync(cancellationToken);
        if (templates.Any(template => Deserialize<Guid>(template.StageIdsJson).Contains(id)))
            return Errors.InventoryTransfer.InvalidOperation("An approval stage linked to a template cannot be deleted.");
        var used = await context.ApprovalRequests.AsNoTracking().AnyAsync(request => request.StageSnapshotsJson.Contains(id.ToString()), cancellationToken);
        if (used) return Errors.InventoryTransfer.InvalidOperation("An approval stage used by an approval request cannot be deleted.");
        context.ApprovalStageDefinitions.Remove(stage);
        await context.SaveChangesAsync(cancellationToken);
        return Result.Deleted;
    }

    public async Task<List<ApprovalTemplateDefinitionDto>> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        await EnsureDefaultsAsync(cancellationToken);
        return (await context.ApprovalTemplateDefinitions.AsNoTracking().OrderByDescending(template => template.Priority).ThenBy(template => template.Name).ToListAsync(cancellationToken))
            .Select(ToDto).ToList();
    }

    public async Task<ErrorOr<ApprovalTemplateDefinitionDto>> SaveTemplateAsync(ApprovalTemplateDefinitionDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.StageIds.Count == 0)
            return Errors.InventoryTransfer.ValidationFailed("Template name and at least one approval stage are required.");
        var validStageCount = await context.ApprovalStageDefinitions.CountAsync(stage => dto.StageIds.Contains(stage.Id), cancellationToken);
        if (validStageCount != dto.StageIds.Distinct().Count())
            return Errors.InventoryTransfer.ValidationFailed("One or more selected approval stages do not exist.");

        var entity = dto.Id == Guid.Empty
            ? new ApprovalTemplateDefinitionEntity()
            : await context.ApprovalTemplateDefinitions.FirstOrDefaultAsync(template => template.Id == dto.Id, cancellationToken);
        if (entity is null)
            return Error.NotFound("ApprovalProcess.TemplateNotFound", "Approval template was not found.");
        entity.Name = dto.Name.Trim();
        entity.Description = dto.Description?.Trim();
        entity.DocumentType = ApprovalDocumentTypes.InventoryTransferRequest;
        entity.OriginatorUserIdsJson = Serialize(dto.OriginatorUserIds.Distinct());
        entity.OriginatorRolesJson = Serialize(dto.OriginatorRoles.Where(role => !string.IsNullOrWhiteSpace(role)).Select(role => role.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        entity.StageIdsJson = Serialize(dto.StageIds.Distinct());
        entity.FromWarehouse = Normalize(dto.FromWarehouse);
        entity.ToWarehouse = Normalize(dto.ToWarehouse);
        entity.Priority = dto.Priority;
        entity.IsActive = dto.IsActive;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (dto.Id == Guid.Empty) context.ApprovalTemplateDefinitions.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task<ErrorOr<Deleted>> DeleteTemplateAsync(Guid id, CancellationToken cancellationToken)
    {
        var template = await context.ApprovalTemplateDefinitions.FindAsync([id], cancellationToken);
        if (template is null) return Result.Deleted;
        if (await context.ApprovalRequests.AsNoTracking().AnyAsync(request => request.ApprovalTemplateId == id, cancellationToken))
            return Errors.InventoryTransfer.InvalidOperation("An approval template used by an approval request cannot be deleted; deactivate it instead.");
        context.ApprovalTemplateDefinitions.Remove(template);
        await context.SaveChangesAsync(cancellationToken);
        return Result.Deleted;
    }

    private async Task EnsureDefaultsAsync(CancellationToken cancellationToken)
    {
        if (await context.ApprovalTemplateDefinitions.AnyAsync(cancellationToken)) return;
        context.ApprovalStageDefinitions.AddRange(
            NewStage(DepotStageId, "Depot Acceptance", "Depot controllers accept transfers arriving from Dispatch.", ApplicationRoles.DepotController),
            NewStage(StockStageId, "Stock Officer Approval", "Stock officers approve transfers initiated by depot controllers.", ApplicationRoles.StockController),
            NewStage(AdminStageId, "Administrator Review", "Fallback review for requests that do not match a specific template.", ApplicationRoles.Admin));
        context.ApprovalTemplateDefinitions.AddRange(
            NewTemplate(DispatchTemplateId, "Dispatch Transfers", 100, [ApplicationRoles.StockController], [DepotStageId]),
            NewTemplate(DepotTemplateId, "Depot Controller Transfers", 100, [ApplicationRoles.DepotController], [StockStageId]),
            NewTemplate(FallbackTemplateId, "General Transfer Review", -100, [], [AdminStageId]));
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { context.ChangeTracker.Clear(); }
    }

    private async Task<ApprovalTemplateDefinitionEntity?> SelectTemplateAsync(User? originator, InventoryTransferRequest document, CancellationToken cancellationToken)
    {
        var templates = await context.ApprovalTemplateDefinitions.AsNoTracking()
            .Where(template => template.IsActive && template.DocumentType == ApprovalDocumentTypes.InventoryTransferRequest)
            .OrderByDescending(template => template.Priority).ToListAsync(cancellationToken);
        return templates.FirstOrDefault(template =>
        {
            var users = Deserialize<Guid>(template.OriginatorUserIdsJson);
            var roles = Deserialize<string>(template.OriginatorRolesJson);
            var originatorMatches = users.Count == 0 && roles.Count == 0 || originator is not null &&
                (users.Contains(originator.Id) || roles.Contains(originator.Role, StringComparer.OrdinalIgnoreCase));
            return originatorMatches && Matches(template.FromWarehouse, document.FromWarehouse) && Matches(template.ToWarehouse, document.ToWarehouse);
        });
    }

    private async Task<User?> ResolveOriginatorAsync(InventoryTransferRequest document, Guid? userId, CancellationToken cancellationToken)
    {
        if (userId.HasValue)
            return await context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == userId.Value, cancellationToken);
        var creation = await context.AuditLogs.AsNoTracking().OrderBy(log => log.Timestamp).FirstOrDefaultAsync(log =>
            log.Action == AuditActions.CreateTransferRequest && log.EntityType == "TransferRequest" &&
            log.EntityId == document.DocEntry.ToString() && log.IsSuccess, cancellationToken);
        if (Guid.TryParse(creation?.UserId, out var auditUserId))
            return await context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == auditUserId, cancellationToken);
        var email = document.RequesterEmail ?? ExtractCommentValue(document.Comments, "Email");
        if (string.IsNullOrWhiteSpace(email)) return null;
        var normalized = email.Trim().ToLowerInvariant();
        return await context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Email != null && user.Email.ToLower() == normalized, cancellationToken);
    }

    private void ApplyProgress(InventoryTransferRequestDto dto, ApprovalRequestEntity request)
    {
        var progress = BuildProgress(request, Deserialize<ApprovalStageSnapshot>(request.StageSnapshotsJson));
        dto.ApprovalRequestId = request.Id;
        dto.ApprovalTemplateName = request.TemplateName;
        dto.RequestedByRole = request.OriginatorRole;
        dto.ApprovalStatus = request.Status;
        dto.ApprovalStages = progress;
        var pendingRoles = progress.Where(stage => stage.Status == ApprovalRequestStatuses.Pending)
            .SelectMany(stage => stage.AuthorizerRoles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        dto.RequiredApproverRole = pendingRoles.Count == 1 ? pendingRoles[0] : pendingRoles.Count > 1 ? "MultipleAuthorizers" : null;
        var latest = request.Decisions.OrderByDescending(decision => decision.DecidedAtUtc).FirstOrDefault();
        dto.DecisionBy = latest?.AuthorizerName;
        dto.DecisionByRole = latest?.AuthorizerRole;
        dto.DecisionAtUtc = latest?.DecidedAtUtc;
    }

    private static List<ApprovalStageProgressDto> BuildProgress(ApprovalRequestEntity request, List<ApprovalStageSnapshot> snapshots)
        => snapshots.Select(stage =>
        {
            var decisions = request.Decisions.Where(decision => decision.StageId == stage.Id).ToList();
            var approvals = decisions.Count(decision => decision.Decision == ApprovalDecisionValues.Approved);
            var rejections = decisions.Count(decision => decision.Decision == ApprovalDecisionValues.NotApproved);
            var status = rejections >= stage.RejectionsRequired ? ApprovalRequestStatuses.NotApproved
                : approvals >= stage.ApprovalsRequired ? ApprovalRequestStatuses.Approved
                : ApprovalRequestStatuses.Pending;
            return new ApprovalStageProgressDto
            {
                StageId = stage.Id, StageName = stage.Name, Description = stage.Description,
                ApprovalsRequired = stage.ApprovalsRequired, RejectionsRequired = stage.RejectionsRequired,
                ApprovalCount = approvals, RejectionCount = rejections, Status = status,
                AuthorizerUserIds = stage.AuthorizerUserIds, AuthorizerRoles = stage.AuthorizerRoles,
                Decisions = decisions.Select(decision => new ApprovalDecisionDto
                {
                    StageId = decision.StageId, AuthorizerUserId = decision.AuthorizerUserId,
                    AuthorizerName = decision.AuthorizerName, AuthorizerRole = decision.AuthorizerRole,
                    Decision = decision.Decision, Remarks = decision.Remarks, DecidedAtUtc = decision.DecidedAtUtc
                }).ToList()
            };
        }).ToList();

    private static bool IsAuthorizer(User user, ApprovalStageProgressDto stage)
        => string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase) ||
           stage.AuthorizerUserIds.Contains(user.Id) ||
           stage.AuthorizerRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase);

    private async Task NotifyPendingAuthorizersAsync(
        ApprovalRequestEntity request,
        List<ApprovalStageSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        try
        {
            var pendingStages = BuildProgress(request, snapshots)
                .Where(stage => stage.Status == ApprovalRequestStatuses.Pending)
                .ToList();
            foreach (var stage in pendingStages)
            {
                var decidedUserIds = stage.Decisions.Select(item => item.AuthorizerUserId).ToHashSet();
                var authorizerUserIds = stage.AuthorizerUserIds;
                var authorizerRoles = stage.AuthorizerRoles;
                var recipients = await context.Users.AsNoTracking()
                    .Where(user => user.IsActive &&
                                   (authorizerUserIds.Contains(user.Id) || authorizerRoles.Contains(user.Role)))
                    .ToListAsync(cancellationToken);

                foreach (var recipient in recipients.Where(user => !decidedUserIds.Contains(user.Id)).DistinctBy(user => user.Id))
                {
                    var entityId = ApprovalNotificationEntityId(request.DocumentKey, stage.StageId);
                    var exists = await context.Notifications.AsNoTracking().AnyAsync(item =>
                        item.Category == "TransferApproval" &&
                        item.EntityType == "TransferRequestApproval" &&
                        item.EntityId == entityId &&
                        item.TargetUsername == recipient.Username,
                        cancellationToken);
                    if (exists) continue;

                    await notificationService.CreateNotificationAsync(new CreateNotificationRequest
                    {
                        Title = $"Transfer #{request.DocumentNumber ?? request.DocumentKey} needs approval",
                        Message = $"{stage.StageName}: {request.FromWarehouse} to {request.ToWarehouse}. " +
                                  $"Requested by {request.OriginatorName}.",
                        Type = "Alert",
                        Category = "TransferApproval",
                        EntityType = "TransferRequestApproval",
                        EntityId = entityId,
                        ActionUrl = $"/inventory-transfers?requestDocEntry={request.DocumentKey}",
                        TargetUserId = recipient.Id,
                        TargetUsername = recipient.Username,
                        Data = new Dictionary<string, string>
                        {
                            ["requestDocEntry"] = request.DocumentKey,
                            ["requestDocNum"] = request.DocumentNumber ?? request.DocumentKey,
                            ["approvalRequestId"] = request.Id.ToString(),
                            ["stageId"] = stage.StageId.ToString(),
                            ["stageName"] = stage.StageName
                        }
                    }, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to notify authorizers for transfer request {DocumentKey}", request.DocumentKey);
        }
    }

    private Task MarkApprovalNotificationsReadAsync(string documentKey, CancellationToken cancellationToken)
        => context.Notifications
            .Where(item => item.Category == "TransferApproval" &&
                           item.EntityType == "TransferRequestApproval" &&
                           item.EntityId != null &&
                           item.EntityId.StartsWith(documentKey + ":") &&
                           !item.IsRead)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.IsRead, true)
                .SetProperty(item => item.ReadAt, DateTime.UtcNow), cancellationToken);

    private static string ApprovalNotificationEntityId(string documentKey, Guid stageId)
        => $"{documentKey}:{stageId:N}";

    private static ApprovalStageDefinitionEntity NewStage(Guid id, string name, string description, string role) => new()
    { Id = id, Name = name, Description = description, AuthorizerRolesJson = Serialize([role]) };
    private static ApprovalTemplateDefinitionEntity NewTemplate(Guid id, string name, int priority, IEnumerable<string> roles, IEnumerable<Guid> stages) => new()
    { Id = id, Name = name, Description = name, Priority = priority, OriginatorRolesJson = Serialize(roles), StageIdsJson = Serialize(stages) };
    private static ApprovalStageSnapshot ToSnapshot(ApprovalStageDefinitionEntity stage) => new()
    { Id = stage.Id, Name = stage.Name, Description = stage.Description, ApprovalsRequired = stage.ApprovalsRequired, RejectionsRequired = stage.RejectionsRequired, AuthorizerUserIds = Deserialize<Guid>(stage.AuthorizerUserIdsJson), AuthorizerRoles = Deserialize<string>(stage.AuthorizerRolesJson) };
    private static ApprovalStageDefinitionDto ToDto(ApprovalStageDefinitionEntity stage) => new()
    { Id = stage.Id, Name = stage.Name, Description = stage.Description, ApprovalsRequired = stage.ApprovalsRequired, RejectionsRequired = stage.RejectionsRequired, AuthorizerUserIds = Deserialize<Guid>(stage.AuthorizerUserIdsJson), AuthorizerRoles = Deserialize<string>(stage.AuthorizerRolesJson), IsActive = stage.IsActive };
    private static ApprovalTemplateDefinitionDto ToDto(ApprovalTemplateDefinitionEntity template) => new()
    { Id = template.Id, Name = template.Name, Description = template.Description, DocumentType = template.DocumentType, OriginatorUserIds = Deserialize<Guid>(template.OriginatorUserIdsJson), OriginatorRoles = Deserialize<string>(template.OriginatorRolesJson), StageIds = Deserialize<Guid>(template.StageIdsJson), FromWarehouse = template.FromWarehouse, ToWarehouse = template.ToWarehouse, Priority = template.Priority, IsActive = template.IsActive };
    private static string Serialize<T>(IEnumerable<T> values) => JsonSerializer.Serialize(values, JsonOptions);
    private static List<T> Deserialize<T>(string? json) { try { return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? []; } catch { return []; } }
    private static bool Matches(string? configured, string? actual) => string.IsNullOrWhiteSpace(configured) || string.Equals(configured.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool IsClosed(string? status) => string.Equals(status, "bost_Close", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase);
    private static string? ExtractCommentValue(string? comments, string label) { if (string.IsNullOrWhiteSpace(comments)) return null; var prefix = $"{label}:"; return comments.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim(); }

    private sealed class ApprovalStageSnapshot
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int ApprovalsRequired { get; set; }
        public int RejectionsRequired { get; set; }
        public List<Guid> AuthorizerUserIds { get; set; } = [];
        public List<string> AuthorizerRoles { get; set; } = [];
    }
}
