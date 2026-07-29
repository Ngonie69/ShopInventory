using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Identifies a document the approval engine can track, independent of where it lives.
/// Inventory transfer requests are keyed by their SAP DocEntry; direct inventory transfers
/// are keyed by the id of the locally held <see cref="PendingInventoryTransferEntity"/>.
/// </summary>
/// <remarks>
/// <c>OriginatorUserId</c> is who raised the document, when the document itself records it.
/// It picks the approval template if no originator is passed in explicitly, so a decision
/// arriving before the approval request exists still routes to the right stages rather than
/// falling through to the catch-all template.
/// </remarks>
public sealed record ApprovalDocumentContext(
    string DocumentType,
    string DocumentKey,
    string? DocumentNumber,
    string? FromWarehouse,
    string? ToWarehouse,
    bool AlreadyClosed = false,
    Guid? OriginatorUserId = null)
{
    public static ApprovalDocumentContext ForTransferRequest(InventoryTransferRequest document) => new(
        ApprovalDocumentTypes.InventoryTransferRequest,
        document.DocEntry.ToString(),
        document.DocNum.ToString(),
        document.FromWarehouse,
        document.ToWarehouse,
        IsClosedStatus(document.DocumentStatus));

    public static ApprovalDocumentContext ForPendingTransfer(PendingInventoryTransferEntity pending) => new(
        ApprovalDocumentTypes.InventoryTransfer,
        pending.Id.ToString(),
        // The draft number is what an authorizer can quote until the transfer posts to SAP.
        pending.SapDocNum?.ToString() ?? pending.DraftNumber,
        pending.FromWarehouse,
        pending.ToWarehouse,
        AlreadyClosed: false,
        OriginatorUserId: pending.CreatedByUserId);

    public static ApprovalDocumentContext ForRequestEdit(PendingTransferRequestEditEntity edit) => new(
        ApprovalDocumentTypes.InventoryTransferRequestEdit,
        edit.Id.ToString(),
        edit.RequestDocNum.ToString(),
        edit.FromWarehouse,
        edit.ToWarehouse,
        AlreadyClosed: false,
        OriginatorUserId: edit.CreatedByUserId);

    private static bool IsClosedStatus(string? status) =>
        string.Equals(status, "bost_Close", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase);
}

public interface IInventoryTransferApprovalService
{
    Task<ApprovalRequestEntity> EnsureRequestAsync(InventoryTransferRequest document, Guid? originatorUserId, CancellationToken cancellationToken);
    Task<ApprovalRequestEntity> EnsureRequestAsync(ApprovalDocumentContext document, Guid? originatorUserId, CancellationToken cancellationToken);
    Task EnrichAsync(IEnumerable<InventoryTransferRequestDto> documents, CancellationToken cancellationToken);
    Task<ErrorOr<ApprovalDecisionOutcomeDto>> SubmitDecisionAsync(
        InventoryTransferRequest document,
        Guid authorizerUserId,
        string decision,
        Guid? stageId,
        string? remarks,
        CancellationToken cancellationToken);
    Task<ErrorOr<ApprovalDecisionOutcomeDto>> SubmitDecisionAsync(
        ApprovalDocumentContext document,
        Guid authorizerUserId,
        string decision,
        Guid? stageId,
        string? remarks,
        CancellationToken cancellationToken);
    /// <summary>
    /// Current approval request and per-stage progress for a document, creating the request if it does not exist yet.
    /// </summary>
    Task<(ApprovalRequestEntity Request, List<ApprovalStageProgressDto> Stages)> GetProgressAsync(
        ApprovalDocumentContext document,
        CancellationToken cancellationToken);
    Task MarkGeneratedAsync(int requestDocEntry, int transferDocEntry, int transferDocNum, Guid generatedByUserId, bool byAuthorizer, CancellationToken cancellationToken);
    Task MarkGeneratedAsync(string documentType, string documentKey, int generatedDocEntry, int generatedDocNum, Guid generatedByUserId, bool byAuthorizer, CancellationToken cancellationToken);
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
    private const string UnknownOriginator = "Unknown";
    // The seed check is cheap but not free, and listing a page asks for it once per document.
    // The service is scoped, so this holds for the length of one request and no longer.
    private readonly HashSet<string> _ensuredDocumentTypes = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid DepotStageId = Guid.Parse("11000000-0000-0000-0000-000000000001");
    private static readonly Guid StockStageId = Guid.Parse("11000000-0000-0000-0000-000000000002");
    private static readonly Guid AdminStageId = Guid.Parse("11000000-0000-0000-0000-000000000003");
    private static readonly Guid DispatchTemplateId = Guid.Parse("22000000-0000-0000-0000-000000000001");
    private static readonly Guid DepotTemplateId = Guid.Parse("22000000-0000-0000-0000-000000000002");
    private static readonly Guid FallbackTemplateId = Guid.Parse("22000000-0000-0000-0000-000000000003");
    private static readonly Guid DirectDepotTemplateId = Guid.Parse("22000000-0000-0000-0000-000000000012");
    private static readonly Guid DirectFallbackTemplateId = Guid.Parse("22000000-0000-0000-0000-000000000013");
    private static readonly Guid EditDepotTemplateId = Guid.Parse("22000000-0000-0000-0000-000000000022");
    private static readonly Guid EditFallbackTemplateId = Guid.Parse("22000000-0000-0000-0000-000000000023");

    public async Task<ApprovalRequestEntity> EnsureRequestAsync(
        InventoryTransferRequest document,
        Guid? originatorUserId,
        CancellationToken cancellationToken)
    {
        var resolvedOriginatorId = originatorUserId
            ?? (await ResolveOriginatorAsync(document, null, cancellationToken))?.Id;
        var request = await EnsureRequestAsync(
            ApprovalDocumentContext.ForTransferRequest(document), resolvedOriginatorId, cancellationToken);
        await BackfillOriginatorNameAsync(request, document.Comments, cancellationToken);
        return request;
    }

    /// <summary>
    /// Older requests carry the requester only in the SAP comments; keep that as the display fallback.
    /// </summary>
    private async Task BackfillOriginatorNameAsync(
        ApprovalRequestEntity request,
        string? comments,
        CancellationToken cancellationToken)
    {
        if (request.OriginatorUserId is not null || request.OriginatorName != UnknownOriginator)
            return;

        var fromComments = ExtractCommentValue(comments, "Requester");
        if (string.IsNullOrWhiteSpace(fromComments))
            return;

        request.OriginatorName = fromComments;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<ApprovalRequestEntity> EnsureRequestAsync(
        ApprovalDocumentContext document,
        Guid? originatorUserId,
        CancellationToken cancellationToken)
    {
        await EnsureDefaultsAsync(document.DocumentType, cancellationToken);
        var documentType = document.DocumentType;
        var documentKey = document.DocumentKey;
        // Tracked explicitly: callers record decisions and move the request's status on the entity
        // returned here, and a no-tracking load would drop those writes without erroring.
        var existing = await context.ApprovalRequests
            .AsTracking()
            .Include(request => request.Decisions)
            .FirstOrDefaultAsync(request =>
                request.DocumentType == documentType &&
                request.DocumentKey == documentKey,
                cancellationToken);
        if (existing is not null)
            return existing;

        var resolvedOriginatorId = originatorUserId ?? document.OriginatorUserId;
        var originator = resolvedOriginatorId.HasValue
            ? await context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == resolvedOriginatorId.Value, cancellationToken)
            : null;
        var template = await SelectTemplateAsync(originator, document, cancellationToken)
            ?? throw new InvalidOperationException($"No active approval template is available for {document.DocumentType}.");
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
            DocumentType = documentType,
            DocumentKey = documentKey,
            DocumentNumber = document.DocumentNumber,
            OriginatorUserId = originator?.Id,
            OriginatorName = originator?.Username ?? UnknownOriginator,
            OriginatorRole = originator?.Role ?? UnknownOriginator,
            FromWarehouse = document.FromWarehouse,
            ToWarehouse = document.ToWarehouse,
            StageSnapshotsJson = Serialize(orderedStages),
            Status = document.AlreadyClosed
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
                .AsTracking()
                .Include(item => item.Decisions)
                .SingleAsync(item =>
                    item.DocumentType == documentType &&
                    item.DocumentKey == documentKey,
                    cancellationToken);
        }
    }

    public async Task EnrichAsync(IEnumerable<InventoryTransferRequestDto> documents, CancellationToken cancellationToken)
    {
        var listed = documents.ToList();
        foreach (var dto in listed)
        {
            dto.RequesterName ??= ExtractCommentValue(dto.Comments, "Requester");
            dto.RequesterEmail ??= ExtractCommentValue(dto.Comments, "Email");
        }

        // Approval requests are opened lazily, so most of a listed page already has one. Reading them
        // in a single query keeps a page of documents off the per-document create path entirely.
        var documentKeys = listed.Select(dto => dto.DocEntry.ToString()).ToList();
        var known = (await context.ApprovalRequests
                .AsTracking()
                .Include(request => request.Decisions)
                .Where(request =>
                    request.DocumentType == ApprovalDocumentTypes.InventoryTransferRequest &&
                    documentKeys.Contains(request.DocumentKey))
                .ToListAsync(cancellationToken))
            .ToDictionary(request => request.DocumentKey);

        foreach (var dto in listed)
        {
            try
            {
                if (known.TryGetValue(dto.DocEntry.ToString(), out var request))
                    await BackfillOriginatorNameAsync(request, dto.Comments, cancellationToken);
                else
                    request = await EnsureRequestAsync(ToModel(dto), null, cancellationToken);

                ApplyProgress(dto, request);
            }
            catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException)
            {
                // Listing is a read: a document the approval configuration cannot route — no matching
                // template, or a template whose stages have all been deactivated — is shown without
                // approval progress rather than failing the page it happens to appear on.
                logger.LogWarning(ex,
                    "Transfer request {DocEntry} has no approval progress; check the approval templates for {DocumentType}",
                    dto.DocEntry, ApprovalDocumentTypes.InventoryTransferRequest);
            }
        }
    }

    public Task<ErrorOr<ApprovalDecisionOutcomeDto>> SubmitDecisionAsync(
        InventoryTransferRequest document,
        Guid authorizerUserId,
        string decision,
        Guid? stageId,
        string? remarks,
        CancellationToken cancellationToken)
        => SubmitDecisionAsync(
            ApprovalDocumentContext.ForTransferRequest(document), authorizerUserId, decision, stageId, remarks, cancellationToken);

    public async Task<(ApprovalRequestEntity Request, List<ApprovalStageProgressDto> Stages)> GetProgressAsync(
        ApprovalDocumentContext document,
        CancellationToken cancellationToken)
    {
        var request = await EnsureRequestAsync(document, null, cancellationToken);
        return (request, BuildProgress(request, Deserialize<ApprovalStageSnapshot>(request.StageSnapshotsJson)));
    }

    public async Task<ErrorOr<ApprovalDecisionOutcomeDto>> SubmitDecisionAsync(
        ApprovalDocumentContext document,
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

        var notificationEntityId = ApprovalNotificationEntityId(request.DocumentType, request.DocumentKey, selectedStage.StageId);
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
            await MarkApprovalNotificationsReadAsync(request.DocumentType, request.DocumentKey, cancellationToken);

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

    public Task MarkGeneratedAsync(
        int requestDocEntry,
        int transferDocEntry,
        int transferDocNum,
        Guid generatedByUserId,
        bool byAuthorizer,
        CancellationToken cancellationToken)
        => MarkGeneratedAsync(
            ApprovalDocumentTypes.InventoryTransferRequest, requestDocEntry.ToString(),
            transferDocEntry, transferDocNum, generatedByUserId, byAuthorizer, cancellationToken);

    public async Task MarkGeneratedAsync(
        string documentType,
        string documentKey,
        int transferDocEntry,
        int transferDocNum,
        Guid generatedByUserId,
        bool byAuthorizer,
        CancellationToken cancellationToken)
    {
        var request = await context.ApprovalRequests.AsTracking().FirstOrDefaultAsync(item =>
            item.DocumentType == documentType &&
            item.DocumentKey == documentKey, cancellationToken);
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
        await EnsureAllDefaultsAsync(cancellationToken);
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
            : await context.ApprovalStageDefinitions.AsTracking().FirstOrDefaultAsync(stage => stage.Id == dto.Id, cancellationToken);
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
        await EnsureAllDefaultsAsync(cancellationToken);
        return (await context.ApprovalTemplateDefinitions.AsNoTracking().OrderByDescending(template => template.Priority).ThenBy(template => template.Name).ToListAsync(cancellationToken))
            .Select(ToDto).ToList();
    }

    public async Task<ErrorOr<ApprovalTemplateDefinitionDto>> SaveTemplateAsync(ApprovalTemplateDefinitionDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || dto.StageIds.Count == 0)
            return Errors.InventoryTransfer.ValidationFailed("Template name and at least one approval stage are required.");
        var documentType = string.IsNullOrWhiteSpace(dto.DocumentType)
            ? ApprovalDocumentTypes.InventoryTransferRequest
            : dto.DocumentType.Trim();
        if (!ApprovalDocumentTypes.IsKnown(documentType))
            return Errors.InventoryTransfer.ValidationFailed(
                $"Document type must be one of: {string.Join(", ", ApprovalDocumentTypes.All)}.");
        var validStageCount = await context.ApprovalStageDefinitions.CountAsync(stage => dto.StageIds.Contains(stage.Id), cancellationToken);
        if (validStageCount != dto.StageIds.Distinct().Count())
            return Errors.InventoryTransfer.ValidationFailed("One or more selected approval stages do not exist.");

        var entity = dto.Id == Guid.Empty
            ? new ApprovalTemplateDefinitionEntity()
            : await context.ApprovalTemplateDefinitions.AsTracking().FirstOrDefaultAsync(template => template.Id == dto.Id, cancellationToken);
        if (entity is null)
            return Error.NotFound("ApprovalProcess.TemplateNotFound", "Approval template was not found.");
        entity.Name = dto.Name.Trim();
        entity.Description = dto.Description?.Trim();
        entity.DocumentType = documentType;
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

    private async Task EnsureAllDefaultsAsync(CancellationToken cancellationToken)
    {
        foreach (var documentType in ApprovalDocumentTypes.All)
            await EnsureDefaultsAsync(documentType, cancellationToken);
    }

    /// <summary>
    /// Seeds the built-in stages and, for the requested document type, its default templates.
    /// Seeding is per document type so that installs created before a document type existed still
    /// pick up its defaults on first use.
    /// </summary>
    /// <remarks>
    /// Every default carries a fixed id and is re-added only when that id is absent, so a default
    /// that was never seeded — or that was later deleted — comes back, while anything an
    /// administrator has since renamed, retargeted or deactivated is left exactly as configured.
    /// Repeating the check is what makes the seed self-healing: it used to run once per document
    /// type and swallow whatever went wrong, which left a type permanently without templates.
    /// </remarks>
    private async Task EnsureDefaultsAsync(string documentType, CancellationToken cancellationToken)
    {
        if (!_ensuredDocumentTypes.Add(documentType)) return;

        var stageIds = await EnsureDefaultStagesAsync(cancellationToken);
        var defaults = documentType switch
        {
            // Direct transfers deliberately do not default to the depot-acceptance stage. Depot
            // controllers are scoped to the warehouse stock leaves from, so routing a transfer
            // *into* their depot to them would leave it with no eligible authorizer. Stock
            // officers are unscoped, and the administrator stage is the catch-all.
            ApprovalDocumentTypes.InventoryTransfer =>
            [
                NewTemplate(DirectDepotTemplateId, "Depot Controller Direct Transfers", documentType, 100, [ApplicationRoles.DepotController], [stageIds[StockStageId]]),
                NewTemplate(DirectFallbackTemplateId, "General Direct Transfer Review", documentType, -100, [], [stageIds[AdminStageId]])
            ],
            // A held edit is raised precisely because the editor does not run the source
            // warehouse, so it must never route back to the depot-controller stage.
            ApprovalDocumentTypes.InventoryTransferRequestEdit =>
            [
                NewTemplate(EditDepotTemplateId, "Depot Controller Request Edits", documentType, 100, [ApplicationRoles.DepotController], [stageIds[StockStageId]]),
                NewTemplate(EditFallbackTemplateId, "General Request Edit Review", documentType, -100, [], [stageIds[AdminStageId]])
            ],
            _ =>
            (ApprovalTemplateDefinitionEntity[])
            [
                NewTemplate(DispatchTemplateId, "Dispatch Transfers", documentType, 100, [ApplicationRoles.StockController], [stageIds[DepotStageId]]),
                NewTemplate(DepotTemplateId, "Depot Controller Transfers", documentType, 100, [ApplicationRoles.DepotController], [stageIds[StockStageId]]),
                NewTemplate(FallbackTemplateId, "General Transfer Review", documentType, -100, [], [stageIds[AdminStageId]])
            ]
        };

        var defaultIds = defaults.Select(template => template.Id).ToList();
        var defaultNames = defaults.Select(template => template.Name).ToList();
        var present = await context.ApprovalTemplateDefinitions.AsNoTracking()
            .Where(template => defaultIds.Contains(template.Id) || defaultNames.Contains(template.Name))
            .Select(template => new { template.Id, template.Name })
            .ToListAsync(cancellationToken);

        // Template names are unique across document types, so a default whose name an administrator
        // has given to something else is skipped rather than allowed to fail the whole insert.
        var missing = defaults
            .Where(template => !present.Any(item => item.Id == template.Id || item.Name == template.Name))
            .ToList();
        if (missing.Count == 0) return;

        context.ApprovalTemplateDefinitions.AddRange(missing);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Another writer seeding the same defaults is the ordinary cause and is harmless, but a
            // silent loss here leaves documents unroutable, so say so.
            logger.LogWarning(ex, "Could not seed the default approval templates for {DocumentType}", documentType);
            context.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Ensures the built-in stages exist and maps each built-in id to the id actually in use.
    /// Stage names are unique, so a stage an install already holds under one of these names is
    /// adopted rather than inserted again — inserting it would fail, and would previously have
    /// taken the templates saved alongside it down with it.
    /// </summary>
    private async Task<Dictionary<Guid, Guid>> EnsureDefaultStagesAsync(CancellationToken cancellationToken)
    {
        ApprovalStageDefinitionEntity[] defaults =
        [
            NewStage(DepotStageId, "Depot Acceptance", "Depot controllers accept transfers arriving from Dispatch.", ApplicationRoles.DepotController),
            NewStage(StockStageId, "Stock Officer Approval", "Stock officers approve transfers initiated by depot controllers.", ApplicationRoles.StockController),
            NewStage(AdminStageId, "Administrator Review", "Fallback review for requests that do not match a specific template.", ApplicationRoles.Admin)
        ];

        var present = await LoadDefaultStagesAsync(defaults, cancellationToken);
        var missing = defaults.Where(stage => Resolve(present, stage) is null).ToList();
        if (missing.Count > 0)
        {
            context.ApprovalStageDefinitions.AddRange(missing);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                logger.LogWarning(ex, "Could not seed the default approval stages");
                context.ChangeTracker.Clear();
            }

            present = await LoadDefaultStagesAsync(defaults, cancellationToken);
        }

        return defaults.ToDictionary(stage => stage.Id, stage => Resolve(present, stage)?.Id ?? stage.Id);
    }

    private async Task<List<ApprovalStageDefinitionEntity>> LoadDefaultStagesAsync(
        IReadOnlyCollection<ApprovalStageDefinitionEntity> defaults,
        CancellationToken cancellationToken)
    {
        var ids = defaults.Select(stage => stage.Id).ToList();
        var names = defaults.Select(stage => stage.Name).ToList();
        return await context.ApprovalStageDefinitions.AsNoTracking()
            .Where(stage => ids.Contains(stage.Id) || names.Contains(stage.Name))
            .ToListAsync(cancellationToken);
    }

    private static ApprovalStageDefinitionEntity? Resolve(
        List<ApprovalStageDefinitionEntity> present,
        ApprovalStageDefinitionEntity stage)
        => present.FirstOrDefault(item => item.Id == stage.Id)
           ?? present.FirstOrDefault(item => item.Name == stage.Name);

    private async Task<ApprovalTemplateDefinitionEntity?> SelectTemplateAsync(User? originator, ApprovalDocumentContext document, CancellationToken cancellationToken)
    {
        var documentType = document.DocumentType;
        var templates = await context.ApprovalTemplateDefinitions.AsNoTracking()
            .Where(template => template.IsActive && template.DocumentType == documentType)
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

    private static InventoryTransferRequest ToModel(InventoryTransferRequestDto dto) => new()
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

                var isDirectTransfer = request.DocumentType == ApprovalDocumentTypes.InventoryTransfer;
                var label = isDirectTransfer ? "Inventory transfer" : "Transfer request";

                foreach (var recipient in recipients.Where(user => !decidedUserIds.Contains(user.Id)).DistinctBy(user => user.Id))
                {
                    var entityId = ApprovalNotificationEntityId(request.DocumentType, request.DocumentKey, stage.StageId);
                    var exists = await context.Notifications.AsNoTracking().AnyAsync(item =>
                        item.Category == "TransferApproval" &&
                        item.EntityType == "TransferRequestApproval" &&
                        item.EntityId == entityId &&
                        item.TargetUsername == recipient.Username,
                        cancellationToken);
                    if (exists) continue;

                    await notificationService.CreateNotificationAsync(new CreateNotificationRequest
                    {
                        Title = isDirectTransfer
                            ? $"Inventory transfer from {request.OriginatorName} needs approval"
                            : $"Transfer #{request.DocumentNumber ?? request.DocumentKey} needs approval",
                        Message = $"{stage.StageName}: {request.FromWarehouse} to {request.ToWarehouse}. " +
                                  $"Requested by {request.OriginatorName}.",
                        Type = "Alert",
                        Category = "TransferApproval",
                        EntityType = "TransferRequestApproval",
                        EntityId = entityId,
                        ActionUrl = isDirectTransfer
                            ? $"/inventory-transfers?pendingTransferId={request.DocumentKey}"
                            : $"/inventory-transfers?requestDocEntry={request.DocumentKey}",
                        TargetUserId = recipient.Id,
                        TargetUsername = recipient.Username,
                        Data = new Dictionary<string, string>
                        {
                            ["documentType"] = request.DocumentType,
                            ["requestDocEntry"] = request.DocumentKey,
                            ["requestDocNum"] = request.DocumentNumber ?? request.DocumentKey,
                            ["approvalRequestId"] = request.Id.ToString(),
                            ["stageId"] = stage.StageId.ToString(),
                            ["stageName"] = stage.StageName,
                            ["documentLabel"] = label
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

    private Task MarkApprovalNotificationsReadAsync(string documentType, string documentKey, CancellationToken cancellationToken)
    {
        var prefix = ApprovalNotificationEntityPrefix(documentType, documentKey);
        return context.Notifications
            .Where(item => item.Category == "TransferApproval" &&
                           item.EntityType == "TransferRequestApproval" &&
                           item.EntityId != null &&
                           item.EntityId.StartsWith(prefix) &&
                           !item.IsRead)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.IsRead, true)
                .SetProperty(item => item.ReadAt, DateTime.UtcNow), cancellationToken);
    }

    // Transfer request notification ids predate multi-document-type support, so their format is
    // preserved verbatim; only newer document types carry a type prefix.
    private static string ApprovalNotificationEntityPrefix(string documentType, string documentKey)
        => documentType == ApprovalDocumentTypes.InventoryTransferRequest
            ? $"{documentKey}:"
            : $"{documentType}:{documentKey}:";

    private static string ApprovalNotificationEntityId(string documentType, string documentKey, Guid stageId)
        => $"{ApprovalNotificationEntityPrefix(documentType, documentKey)}{stageId:N}";

    private static ApprovalStageDefinitionEntity NewStage(Guid id, string name, string description, string role) => new()
    { Id = id, Name = name, Description = description, AuthorizerRolesJson = Serialize([role]) };
    private static ApprovalTemplateDefinitionEntity NewTemplate(Guid id, string name, string documentType, int priority, IEnumerable<string> roles, IEnumerable<Guid> stages) => new()
    { Id = id, Name = name, Description = name, DocumentType = documentType, Priority = priority, OriginatorRolesJson = Serialize(roles), StageIdsJson = Serialize(stages) };
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
