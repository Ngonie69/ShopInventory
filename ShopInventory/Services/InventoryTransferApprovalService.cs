using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers;
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
/// <para>
/// A transfer's lines can each name their own warehouses — one document can carry stock out of four
/// dispatch warehouses into the factory shop — so the header pair is not the whole route.
/// <c>LineFromWarehouses</c> and <c>LineToWarehouses</c> carry each line's own codes when the caller
/// has the lines; <see cref="SourceWarehouses"/> and <see cref="DestinationWarehouses"/> are what
/// templates and warehouse-scoped stages are matched against.
/// </para>
/// </remarks>
public sealed record ApprovalDocumentContext(
    string DocumentType,
    string DocumentKey,
    string? DocumentNumber,
    string? FromWarehouse,
    string? ToWarehouse,
    bool AlreadyClosed = false,
    Guid? OriginatorUserId = null,
    IReadOnlyList<string?>? LineFromWarehouses = null,
    IReadOnlyList<string?>? LineToWarehouses = null)
{
    /// <summary>Every warehouse the document draws from: each line's own, or the header's when no line names one.</summary>
    public IReadOnlyList<string> SourceWarehouses => DistinctCodes(LineFromWarehouses, FromWarehouse);

    /// <summary>Every warehouse the document delivers to: each line's own, or the header's when no line names one.</summary>
    public IReadOnlyList<string> DestinationWarehouses => DistinctCodes(LineToWarehouses, ToWarehouse);

    public static ApprovalDocumentContext ForTransferRequest(InventoryTransferRequest document) => new(
        ApprovalDocumentTypes.InventoryTransferRequest,
        document.DocEntry.ToString(),
        document.DocNum.ToString(),
        document.FromWarehouse,
        document.ToWarehouse,
        IsClosedStatus(document.DocumentStatus),
        LineFromWarehouses: document.StockTransferLines?.Select(line => line.FromWarehouseCode ?? document.FromWarehouse).ToList(),
        LineToWarehouses: document.StockTransferLines?.Select(line => line.WarehouseCode ?? document.ToWarehouse).ToList());

    public static ApprovalDocumentContext ForPendingTransfer(PendingInventoryTransferEntity pending)
    {
        var (lineFrom, lineTo) = PendingLineWarehouses(pending);
        return new(
            ApprovalDocumentTypes.InventoryTransfer,
            pending.Id.ToString(),
            // The draft number is what an authorizer can quote until the transfer posts to SAP.
            pending.SapDocNum?.ToString() ?? pending.DraftNumber,
            pending.FromWarehouse,
            pending.ToWarehouse,
            AlreadyClosed: false,
            OriginatorUserId: pending.CreatedByUserId,
            LineFromWarehouses: lineFrom,
            LineToWarehouses: lineTo);
    }

    /// <summary>
    /// A document not yet raised, described by its warehouses alone — what the transfer form asks
    /// about to show who will be asked to approve it.
    /// </summary>
    public static ApprovalDocumentContext ForPreview(
        string documentType,
        IReadOnlyList<string> fromWarehouses,
        IReadOnlyList<string> toWarehouses,
        Guid? originatorUserId) => new(
        documentType,
        DocumentKey: "preview",
        DocumentNumber: null,
        fromWarehouses.FirstOrDefault(),
        toWarehouses.FirstOrDefault(),
        OriginatorUserId: originatorUserId,
        LineFromWarehouses: fromWarehouses.ToList<string?>(),
        LineToWarehouses: toWarehouses.ToList<string?>());

    private static (IReadOnlyList<string?>? From, IReadOnlyList<string?>? To) PendingLineWarehouses(
        PendingInventoryTransferEntity pending)
    {
        try
        {
            var payload = PendingInventoryTransferMapper.DeserializePayload(pending);
            return (
                payload.Lines!.Select(line => line.FromWarehouseCode ?? pending.FromWarehouse).ToList<string?>(),
                payload.Lines!.Select(line => line.ToWarehouseCode ?? pending.ToWarehouse).ToList<string?>());
        }
        catch (InvalidOperationException)
        {
            // An unreadable payload cannot post either; route it on the header rather than not at all.
            return (null, null);
        }
    }

    private static IReadOnlyList<string> DistinctCodes(IReadOnlyList<string?>? lineCodes, string? headerCode)
    {
        var codes = (lineCodes ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count > 0)
            return codes;
        return string.IsNullOrWhiteSpace(headerCode) ? [] : [headerCode.Trim()];
    }

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

    /// <summary>
    /// Whether an approval has ever been opened against a document — which is what tells a request
    /// this app raised from one raised directly in SAP, since both create paths open an approval as
    /// the document is created.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GetProgressAsync"/> this only reads. Asking whether this app is the gate
    /// for a document must never be the thing that makes it one.
    /// </remarks>
    Task<bool> HasApprovalAsync(ApprovalDocumentContext document, CancellationToken cancellationToken);

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
    /// <summary>
    /// Whether an approval request has already been opened for a document — a read, unlike
    /// <see cref="GetProgressAsync"/>, which opens one. This app is the approval gate only for
    /// documents it raised itself, so this is how a caller tells those apart from a document
    /// that arrived from SAP without ever passing through the approval process.
    /// </summary>
    Task<bool> HasApprovalAsync(string documentType, string documentKey, CancellationToken cancellationToken);
    /// <summary>
    /// Whether the user is an authorizer on a stage still awaiting a decision — <paramref name="stageId"/>
    /// when given, any pending stage otherwise. By role and name only: someone barred from approving
    /// their own document still counts, so <see cref="SubmitDecisionAsync(ApprovalDocumentContext, Guid, string, Guid?, string?, CancellationToken)"/>
    /// can tell them why rather than leave them reading "awaiting" a stage they hold.
    /// </summary>
    Task<bool> CanAuthorizePendingStageAsync(ApprovalDocumentContext document, Guid userId, Guid? stageId, CancellationToken cancellationToken);

    /// <summary>
    /// Sends an approved document back to its approvers because it has changed since they approved
    /// it. Every earlier decision is cleared, the approvers are told again, and the person who made
    /// the change may not approve it.
    /// </summary>
    Task<ApprovalRequestEntity> ReopenAsync(ApprovalDocumentContext document, Guid reopenedByUserId, CancellationToken cancellationToken);
    Task MarkGeneratedAsync(int requestDocEntry, int transferDocEntry, int transferDocNum, Guid generatedByUserId, bool byAuthorizer, CancellationToken cancellationToken);
    Task MarkGeneratedAsync(string documentType, string documentKey, int generatedDocEntry, int generatedDocNum, Guid generatedByUserId, bool byAuthorizer, CancellationToken cancellationToken);
    Task<List<ApprovalStageDefinitionDto>> GetStagesAsync(CancellationToken cancellationToken);
    Task<ErrorOr<ApprovalStageDefinitionDto>> SaveStageAsync(ApprovalStageDefinitionDto stage, CancellationToken cancellationToken);
    Task<ErrorOr<Deleted>> DeleteStageAsync(Guid id, CancellationToken cancellationToken);
    Task<List<ApprovalTemplateDefinitionDto>> GetTemplatesAsync(CancellationToken cancellationToken);
    Task<ErrorOr<ApprovalTemplateDefinitionDto>> SaveTemplateAsync(ApprovalTemplateDefinitionDto template, CancellationToken cancellationToken);
    Task<ErrorOr<Deleted>> DeleteTemplateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Which template and stages a document with these warehouses would be routed to, and whether
    /// each stage has anyone who could decide it. Opens nothing.
    /// </summary>
    Task<ApprovalRoutePreviewDto> PreviewRouteAsync(ApprovalDocumentContext document, CancellationToken cancellationToken);
}

public sealed class InventoryTransferApprovalService(
    ApplicationDbContext context,
    INotificationService notificationService,
    ILogger<InventoryTransferApprovalService> logger)
    : IInventoryTransferApprovalService
{
    private const string UnknownOriginator = "Unknown";
    // Kept apart from the authorizers' "needs approval" entity type so the read sweeps that clear
    // those never swallow the outcome the originator has just been told about.
    private const string ApprovalOutcomeEntityType = "TransferApprovalOutcome";
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
        var orderedStages = await TemplateSnapshotsAsync(template, document, cancellationToken);
        if (orderedStages.Count == 0)
            throw new InvalidOperationException($"Approval template '{template.Name}' has no active approval stages.");
        await AddReceivingOwnerStageAsync(orderedStages, document, originator, cancellationToken);

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
            FromWarehouse = RouteLabel(document.SourceWarehouses, document.FromWarehouse),
            ToWarehouse = RouteLabel(document.DestinationWarehouses, document.ToWarehouse),
            StageSnapshotsJson = Serialize(orderedStages),
            Status = document.AlreadyClosed
                ? ApprovalRequestStatuses.Generated
                // Only when every stage is already met by the requester's own consent — a template
                // whose one stage is the receiving owner, raised by that owner. Any other stage
                // needs a decision, so this is Pending in every ordinary case.
                : BuildProgress(new ApprovalRequestEntity(), orderedStages)
                    .All(stage => stage.Status == ApprovalRequestStatuses.Approved)
                    ? ApprovalRequestStatuses.Approved
                    : ApprovalRequestStatuses.Pending
        };
        if (request.Status == ApprovalRequestStatuses.Approved)
            request.CompletedAtUtc = DateTime.UtcNow;
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

    public Task<bool> HasApprovalAsync(ApprovalDocumentContext document, CancellationToken cancellationToken)
    {
        var documentType = document.DocumentType;
        var documentKey = document.DocumentKey;
        return context.ApprovalRequests
            .AsNoTracking()
            .AnyAsync(request =>
                request.DocumentType == documentType &&
                request.DocumentKey == documentKey,
                cancellationToken);
    }

    public async Task EnrichAsync(IEnumerable<InventoryTransferRequestDto> documents, CancellationToken cancellationToken)
    {
        var listed = documents.ToList();
        foreach (var dto in listed)
        {
            dto.RequesterName ??= ExtractCommentValue(dto.Comments, "Requester");
            dto.RequesterEmail ??= ExtractCommentValue(dto.Comments, "Email");
        }

        // Listing reports approval progress; it never starts it. This app is the approval gate only
        // for requests it raised itself, and those open an approval request as they are created. A
        // request raised directly in SAP has none, and opening one on its behalf here labelled it
        // "Awaiting Administrator Review" — the catch-all stage, since a SAP-raised document has no
        // originator to route on — and notified the administrators about a document nobody had asked
        // them to review. Requests the app did not raise are left showing the status SAP reports.
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
            if (!known.TryGetValue(dto.DocEntry.ToString(), out var request))
                continue;

            try
            {
                await BackfillOriginatorNameAsync(request, dto.Comments, cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                // Listing is a read: a requester name that will not save is not worth failing the
                // page over, and the progress below still is worth showing.
                logger.LogWarning(ex,
                    "Could not record the requester for transfer request {DocEntry} from its comments",
                    dto.DocEntry);
                context.ChangeTracker.Clear();
            }

            ApplyProgress(dto, request);
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

    public Task<bool> HasApprovalAsync(string documentType, string documentKey, CancellationToken cancellationToken)
        => context.ApprovalRequests.AsNoTracking().AnyAsync(
            request => request.DocumentType == documentType && request.DocumentKey == documentKey,
            cancellationToken);

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

        // Approval is a second pair of eyes, so the person who raised a document, or who changed it
        // after it was approved, cannot be that pair. Turning their own document down is allowed:
        // that only stops it, which they could do by withdrawing it anyway.
        if (decision == ApprovalDecisionValues.Approved && OwnDecisionError(request, authorizer.Id) is { } own)
            return own;

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
        // Which of the document's warehouses this decision speaks for, fixed now: someone moved to
        // another warehouse later has still approved for the one they ran when they decided.
        existingDecision.WarehouseCodesJson = IsScoped(selectedStage)
            ? Serialize(CoveredWarehouses(authorizer, selectedStage))
            : null;
        existingDecision.DecidedAtUtc = DateTime.UtcNow;
        if (decision == ApprovalDecisionValues.Approved)
            RecordOwnerAgreement(request, snapshots, progress, selectedStage, authorizer);

        progress = BuildProgress(request, snapshots);
        var stageAfter = progress.First(stage => stage.StageId == selectedStage.StageId);
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
        {
            await NotifyPendingAuthorizersAsync(request, snapshots, cancellationToken);
        }
        else
        {
            await MarkApprovalNotificationsReadAsync(request.DocumentType, request.DocumentKey, cancellationToken);
            await NotifyOriginatorOfDecisionAsync(request, authorizer, existingDecision, cancellationToken);
        }

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
                _ when stageAfter.Status == ApprovalRequestStatuses.Pending && stageAfter.WarehouseProgress.Count > 0 =>
                    $"Decision recorded for {selectedStage.StageName}; still waiting on " +
                    string.Join(", ", stageAfter.WarehouseProgress
                        .Where(item => item.Status == ApprovalRequestStatuses.Pending)
                        .Select(item => item.WarehouseCode)) + ".",
                _ => $"Decision recorded for {selectedStage.StageName}; other approvals are still pending."
            }
        };
    }

    /// <summary>
    /// The owner of a receiving warehouse who approves another stage of a request has agreed to take
    /// the stock, so that approval is recorded against the receiving-owner stage too, for the waiting
    /// warehouses they run.
    /// </summary>
    /// <remarks>
    /// Without it, a request a stock officer raises into a depot asks that depot's controller twice —
    /// once as the template's "Depot Acceptance", once as the depot's owner — for one judgement.
    /// Administrators are not owners by role, so an administrator's approval of their own stage never
    /// signs for a warehouse; they approve the receiving stage on it, by choice.
    /// </remarks>
    private static void RecordOwnerAgreement(
        ApprovalRequestEntity request,
        List<ApprovalStageSnapshot> snapshots,
        List<ApprovalStageProgressDto> progressBeforeDecision,
        ApprovalStageProgressDto decidedStage,
        User authorizer)
    {
        foreach (var snapshot in snapshots.Where(item => item.FollowsReceivingOwnerRule && item.Id != decidedStage.StageId))
        {
            var stage = progressBeforeDecision.FirstOrDefault(item => item.StageId == snapshot.Id);
            if (stage is null || stage.Status != ApprovalRequestStatuses.Pending)
                continue;
            if (request.Decisions.Any(item => item.StageId == snapshot.Id && item.AuthorizerUserId == authorizer.Id))
                continue;

            var waiting = stage.WarehouseProgress.Count > 0
                ? stage.WarehouseProgress.Where(item => item.Status == ApprovalRequestStatuses.Pending).Select(item => item.WarehouseCode)
                : stage.ScopeWarehouses;
            if (ReceivingWarehouseOwnerRule.OwnedBy(authorizer, snapshot.AuthorizerRoles, snapshot.AuthorizerUserIds, waiting).Count == 0)
                continue;

            request.Decisions.Add(new ApprovalDecisionEntity
            {
                ApprovalRequestId = request.Id,
                StageId = snapshot.Id,
                StageName = snapshot.Name,
                AuthorizerUserId = authorizer.Id,
                AuthorizerName = authorizer.Username,
                AuthorizerRole = authorizer.Role,
                Decision = ApprovalDecisionValues.Approved,
                Remarks = $"Agreed as the receiving warehouse's owner when approving {decidedStage.StageName}.",
                // Every scope warehouse they run, as an ordinary decision on this stage would store.
                WarehouseCodesJson = Serialize(ReceivingWarehouseOwnerRule.OwnedBy(
                    authorizer, snapshot.AuthorizerRoles, snapshot.AuthorizerUserIds, snapshot.ScopeWarehouses)),
                DecidedAtUtc = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Why <paramref name="userId"/> may not approve <paramref name="request"/>, or null when they may.
    /// </summary>
    /// <remarks>
    /// Admin is an authorizer on every stage, and with the default templates every document an
    /// administrator raises goes to Administrator Review, so without this one administrator could
    /// raise, approve and post a transfer with nobody else looking at it.
    /// </remarks>
    public static Error? OwnDecisionError(ApprovalRequestEntity request, Guid userId)
    {
        if (request.OriginatorUserId == userId)
            return Errors.InventoryTransfer.OwnDocumentApprovalNotAllowed(DocumentLabel(request.DocumentType).ToLowerInvariant());
        if (request.ReopenedByUserId == userId)
            return Errors.InventoryTransfer.OwnChangeApprovalNotAllowed(DocumentLabel(request.DocumentType).ToLowerInvariant());
        return null;
    }

    /// <summary>Whether <paramref name="userId"/> may approve <paramref name="request"/> at all.</summary>
    public static bool MayApprove(ApprovalRequestEntity request, Guid userId) => OwnDecisionError(request, userId) is null;

    public async Task<bool> CanAuthorizePendingStageAsync(
        ApprovalDocumentContext document,
        Guid userId,
        Guid? stageId,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == userId && item.IsActive, cancellationToken);
        if (user is null)
            return false;

        var (_, stages) = await GetProgressAsync(document, cancellationToken);
        return stages.Any(stage =>
            stage.Status == ApprovalRequestStatuses.Pending &&
            (stageId is null || stage.StageId == stageId) &&
            IsAuthorizer(user, stage));
    }

    public async Task<ApprovalRequestEntity> ReopenAsync(
        ApprovalDocumentContext document,
        Guid reopenedByUserId,
        CancellationToken cancellationToken)
    {
        var request = await EnsureRequestAsync(document, null, cancellationToken);
        if (request.Status is ApprovalRequestStatuses.Generated or ApprovalRequestStatuses.GeneratedByAuthorizer or ApprovalRequestStatuses.Cancelled)
            throw new InvalidOperationException(
                $"The approval for {DocumentLabel(request.DocumentType).ToLowerInvariant()} {request.DocumentNumber ?? request.DocumentKey} is already {request.Status} and cannot be reopened.");

        // The approvals were given to the document as it was. Kept, they would count towards the
        // changed one and it would read as approved again before anyone had looked. The audit log
        // still holds each of them.
        context.ApprovalDecisions.RemoveRange(request.Decisions);
        request.Decisions.Clear();
        request.Status = ApprovalRequestStatuses.Pending;
        request.CompletedAtUtc = null;
        request.ReopenedByUserId = reopenedByUserId;
        request.ReopenedAtUtc = DateTime.UtcNow;

        // The change may have put stock on another warehouse, so a warehouse-scoped stage is aimed at
        // the document as it now stands rather than as it was first raised.
        var snapshots = Deserialize<ApprovalStageSnapshot>(request.StageSnapshotsJson);
        foreach (var snapshot in snapshots)
            snapshot.ScopeWarehouses = ScopeWarehousesFor(snapshot.WarehouseScope, document);
        var originator = request.OriginatorUserId is { } originatorId
            ? await context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == originatorId, cancellationToken)
            : null;
        // Re-aimed by the same rule it was opened under — and added if the request predates it, or
        // first delivered only to vans and now delivers somewhere that has an owner.
        await AddReceivingOwnerStageAsync(snapshots, document, originator, cancellationToken);
        request.StageSnapshotsJson = Serialize(snapshots);
        request.FromWarehouse = RouteLabel(document.SourceWarehouses, document.FromWarehouse);
        request.ToWarehouse = RouteLabel(document.DestinationWarehouses, document.ToWarehouse);
        await context.SaveChangesAsync(cancellationToken);

        // The approvers' earlier alerts were cleared when the approval completed, and one alert per
        // stage is all a person is sent, so they are removed to let the new round reach them. The
        // originator's "approved" is no longer true either.
        var prefix = ApprovalNotificationEntityPrefix(request.DocumentType, request.DocumentKey);
        await context.Notifications
            .Where(item => item.Category == "TransferApproval" &&
                           item.EntityType == "TransferRequestApproval" &&
                           item.EntityId != null &&
                           item.EntityId.StartsWith(prefix))
            .ExecuteDeleteAsync(cancellationToken);
        await MarkOriginatorOutcomesReadAsync(request, cancellationToken);
        await NotifyPendingAuthorizersAsync(request, snapshots, cancellationToken);
        return request;
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

        // Nothing is waiting on an authorizer once the document exists, so any alert still sitting
        // unread on their bell is asking for a decision that can no longer be taken. A request
        // generated straight from an authorizer's own approval already cleared them; one that came
        // through any other route — a conversion, a retry, a poster picking up a queued transfer —
        // left them nagging indefinitely.
        await MarkApprovalNotificationsReadAsync(documentType, documentKey, cancellationToken);
        await NotifyOriginatorOfGenerationAsync(request, generatedByUserId, transferDocNum, cancellationToken);
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
        entity.WarehouseScope = ApprovalWarehouseScopes.Normalize(dto.WarehouseScope);
        // Approvals per warehouse only mean something when the stage is tied to warehouses.
        entity.ApprovalPerWarehouse = dto.ApprovalPerWarehouse && entity.WarehouseScope != ApprovalWarehouseScopes.Any;
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
        entity.FromWarehousesJson = Serialize(NormalizeCodes(dto.FromWarehouses));
        entity.ToWarehousesJson = Serialize(NormalizeCodes(dto.ToWarehouses));
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

    public async Task<ApprovalRoutePreviewDto> PreviewRouteAsync(
        ApprovalDocumentContext document,
        CancellationToken cancellationToken)
    {
        await EnsureDefaultsAsync(document.DocumentType, cancellationToken);
        var originator = document.OriginatorUserId is { } originatorId
            ? await context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == originatorId, cancellationToken)
            : null;
        var template = await SelectTemplateAsync(originator, document, cancellationToken);
        if (template is null)
            return new ApprovalRoutePreviewDto { Matched = false };

        var snapshots = await TemplateSnapshotsAsync(template, document, cancellationToken);
        await AddReceivingOwnerStageAsync(snapshots, document, originator, cancellationToken);

        // One read of the people who could be asked, filtered per stage below. Administrators are
        // left out of the count: they can always decide, and counting them would hide a stage that
        // otherwise has nobody.
        var roles = snapshots.SelectMany(stage => stage.AuthorizerRoles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var userIds = snapshots.SelectMany(stage => stage.AuthorizerUserIds).Distinct().ToList();
        var candidates = await context.Users.AsNoTracking()
            .Where(user => user.IsActive && user.Role != ApplicationRoles.Admin &&
                           (userIds.Contains(user.Id) || roles.Contains(user.Role)))
            .ToListAsync(cancellationToken);

        var preview = new ApprovalRoutePreviewDto { Matched = true, TemplateId = template.Id, TemplateName = template.Name };
        foreach (var snapshot in snapshots)
        {
            var stage = BuildProgress(new ApprovalRequestEntity(), [snapshot])[0];
            // Whoever raises the document cannot approve it, so they are no help to this stage.
            var eligible = candidates
                .Where(user => user.Id != originator?.Id && IsAuthorizer(user, stage))
                .ToList();
            preview.Stages.Add(new ApprovalRoutePreviewStageDto
            {
                StageId = stage.StageId,
                StageName = stage.StageName,
                ApprovalsRequired = snapshot.ApprovalsRequired,
                AuthorizerRoles = stage.AuthorizerRoles,
                WarehouseScope = stage.WarehouseScope,
                ApprovalPerWarehouse = stage.ApprovalPerWarehouse,
                ScopeWarehouses = stage.ScopeWarehouses,
                // A warehouse the raiser runs is approved by their raising it, so it needs nobody.
                WarehousesWithoutAuthorizer = IsScoped(stage)
                    ? stage.ScopeWarehouses
                        .Where(code => !snapshot.ConsentedWarehouses.Contains(code, StringComparer.OrdinalIgnoreCase) &&
                                       !eligible.Any(user => CoveredWarehouses(user, stage).Contains(code, StringComparer.OrdinalIgnoreCase)))
                        .ToList()
                    : [],
                AuthorizerCount = eligible.Count
            });
        }

        return preview;
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
            NewStage(AdminStageId, "Administrator Review", "Fallback review for requests that do not match a specific template.", ApplicationRoles.Admin),
            // Added to transfer requests by the engine rather than by a template; seeded here so it
            // comes back if deleted. Deactivating it is how the rule is switched off.
            new ApprovalStageDefinitionEntity
            {
                Id = ReceivingWarehouseOwnerRule.StageId,
                Name = ReceivingWarehouseOwnerRule.StageName,
                Description = ReceivingWarehouseOwnerRule.StageDescription,
                AuthorizerRolesJson = Serialize(ReceivingWarehouseOwnerRule.DefaultRoles),
                WarehouseScope = ApprovalWarehouseScopes.Destination,
                ApprovalPerWarehouse = true
            }
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

    /// <summary>The template's own active stages, in its order, aimed at this document.</summary>
    private async Task<List<ApprovalStageSnapshot>> TemplateSnapshotsAsync(
        ApprovalTemplateDefinitionEntity template,
        ApprovalDocumentContext document,
        CancellationToken cancellationToken)
    {
        var stageIds = Deserialize<Guid>(template.StageIdsJson);
        var stages = await context.ApprovalStageDefinitions.AsNoTracking()
            .Where(stage => stage.IsActive && stageIds.Contains(stage.Id))
            .ToListAsync(cancellationToken);
        return stageIds
            .Select(stageId => stages.FirstOrDefault(stage => stage.Id == stageId))
            .Where(stage => stage is not null)
            .Select(stage => ToSnapshot(stage!, document))
            .ToList();
    }

    /// <summary>
    /// Puts <see cref="ReceivingWarehouseOwnerRule"/> into a transfer request's stages: aims a
    /// receiving-owner stage the template already holds, or adds one after the template's own. Leaves
    /// it out when every destination is a van, and when an administrator has switched it off.
    /// </summary>
    /// <remarks>
    /// Other document types are left exactly as their template says. A direct transfer is raised by
    /// the person moving the stock, and its templates already name who receives it where that matters.
    /// </remarks>
    private async Task AddReceivingOwnerStageAsync(
        List<ApprovalStageSnapshot> snapshots,
        ApprovalDocumentContext document,
        User? originator,
        CancellationToken cancellationToken)
    {
        if (document.DocumentType != ApprovalDocumentTypes.InventoryTransferRequest)
            return;

        if (!snapshots.Any(snapshot => snapshot.Id == ReceivingWarehouseOwnerRule.StageId))
        {
            var definition = await context.ApprovalStageDefinitions.AsNoTracking()
                .FirstOrDefaultAsync(stage => stage.Id == ReceivingWarehouseOwnerRule.StageId && stage.IsActive, cancellationToken);
            if (definition is null)
                return;
            snapshots.Add(ToSnapshot(definition, document));
        }

        foreach (var snapshot in snapshots.Where(snapshot => snapshot.Id == ReceivingWarehouseOwnerRule.StageId))
        {
            snapshot.WarehouseScope = ApprovalWarehouseScopes.Destination;
            snapshot.FollowsReceivingOwnerRule = true;
            snapshot.ScopeWarehouses = ReceivingWarehouseOwnerRule.WarehousesToApprove(document.DestinationWarehouses);
            snapshot.ConsentedWarehouses = ReceivingWarehouseOwnerRule.OwnedBy(
                originator, snapshot.AuthorizerRoles, snapshot.AuthorizerUserIds, snapshot.ScopeWarehouses);
        }

        // Nothing but vans: nobody's agreement is needed. Left in with no warehouses, the stage would
        // fall back to its roles alone and wait for any stock officer anywhere.
        snapshots.RemoveAll(snapshot => snapshot.FollowsReceivingOwnerRule && snapshot.ScopeWarehouses.Count == 0);
    }

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
            return originatorMatches &&
                   MatchesEvery(Deserialize<string>(template.FromWarehousesJson), document.SourceWarehouses) &&
                   MatchesEvery(Deserialize<string>(template.ToWarehousesJson), document.DestinationWarehouses);
        });
    }

    /// <summary>
    /// A template's warehouse list holds a document only when every warehouse the document uses on
    /// that end is on the list. Checking any one of them instead would let a line from an unlisted
    /// warehouse ride through on the strength of another line's.
    /// </summary>
    private static bool MatchesEvery(IReadOnlyList<string> configured, IReadOnlyList<string> actual)
        => configured.Count == 0 ||
           actual.Count > 0 && actual.All(code => configured.Contains(code, StringComparer.OrdinalIgnoreCase));

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
        dto.OriginatorUserId = request.OriginatorUserId;
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
            var scope = ApprovalWarehouseScopes.Normalize(stage.WarehouseScope);
            List<string> scopeWarehouses = scope == ApprovalWarehouseScopes.Any ? [] : stage.ScopeWarehouses;

            // Per-warehouse stages count each warehouse on its own: a return to DISPYOG and DISPCH is
            // approved only once both have their approvals, however many come from one side.
            // A warehouse the requester runs is approved by their raising the request.
            bool Consented(string code) => stage.ConsentedWarehouses.Contains(code, StringComparer.OrdinalIgnoreCase);
            List<ApprovalWarehouseProgressDto> warehouseProgress = stage.ApprovalPerWarehouse && scopeWarehouses.Count > 0
                ? scopeWarehouses.Select(code =>
                {
                    var consented = Consented(code);
                    var count = consented
                        ? stage.ApprovalsRequired
                        : decisions.Count(decision =>
                            decision.Decision == ApprovalDecisionValues.Approved &&
                            Deserialize<string>(decision.WarehouseCodesJson).Contains(code, StringComparer.OrdinalIgnoreCase));
                    return new ApprovalWarehouseProgressDto
                    {
                        WarehouseCode = code,
                        ApprovalCount = count,
                        ApprovalsRequired = stage.ApprovalsRequired,
                        Status = count >= stage.ApprovalsRequired ? ApprovalRequestStatuses.Approved : ApprovalRequestStatuses.Pending,
                        ConsentedByRequester = consented
                    };
                }).ToList()
                : [];

            var approved = warehouseProgress.Count > 0
                ? warehouseProgress.All(item => item.Status == ApprovalRequestStatuses.Approved)
                : approvals >= stage.ApprovalsRequired ||
                  (scopeWarehouses.Count > 0 && scopeWarehouses.All(Consented));
            var status = rejections >= stage.RejectionsRequired ? ApprovalRequestStatuses.NotApproved
                : approved ? ApprovalRequestStatuses.Approved
                : ApprovalRequestStatuses.Pending;
            return new ApprovalStageProgressDto
            {
                StageId = stage.Id, StageName = stage.Name, Description = stage.Description,
                // Read as "n of m" on screen, so a per-warehouse stage reports its warehouses' total.
                ApprovalsRequired = warehouseProgress.Count > 0 ? warehouseProgress.Sum(item => item.ApprovalsRequired) : stage.ApprovalsRequired,
                RejectionsRequired = stage.RejectionsRequired,
                ApprovalCount = warehouseProgress.Count > 0 ? warehouseProgress.Sum(item => Math.Min(item.ApprovalCount, item.ApprovalsRequired)) : approvals,
                RejectionCount = rejections, Status = status,
                AuthorizerUserIds = stage.AuthorizerUserIds, AuthorizerRoles = stage.AuthorizerRoles,
                WarehouseScope = scope,
                ApprovalPerWarehouse = stage.ApprovalPerWarehouse,
                ScopeWarehouses = scopeWarehouses,
                WarehouseProgress = warehouseProgress,
                Decisions = decisions.Select(decision => new ApprovalDecisionDto
                {
                    StageId = decision.StageId, AuthorizerUserId = decision.AuthorizerUserId,
                    AuthorizerName = decision.AuthorizerName, AuthorizerRole = decision.AuthorizerRole,
                    Decision = decision.Decision, Remarks = decision.Remarks,
                    WarehouseCodes = Deserialize<string>(decision.WarehouseCodesJson),
                    DecidedAtUtc = decision.DecidedAtUtc
                }).ToList()
            };
        }).ToList();

    /// <summary>
    /// Whether <paramref name="user"/> may decide <paramref name="stage"/>: an administrator always; anyone
    /// else when the stage names them or their role and, on a warehouse-scoped stage, their account is
    /// assigned to one of the document's warehouses on the stage's end.
    /// </summary>
    /// <remarks>
    /// The one rule for this, used by the decision itself, the queue's "can decide" flag, the
    /// notifications and the route preview. Those kept their own copies before, and a copy that missed
    /// the warehouse check would offer an Approve button the decision then refuses.
    /// </remarks>
    public static bool IsAuthorizer(User user, ApprovalStageProgressDto stage)
    {
        if (IsAdministrator(user))
            return true;

        var holdsStage = stage.AuthorizerUserIds.Contains(user.Id) ||
                         stage.AuthorizerRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase);
        return holdsStage && (!IsScoped(stage) || CoveredWarehouses(user, stage).Count > 0);
    }

    /// <summary>
    /// The document's warehouses on a scoped stage's end that <paramref name="user"/> speaks for: those
    /// assigned to their account, or every one of them for an administrator.
    /// </summary>
    public static IReadOnlyList<string> CoveredWarehouses(User user, ApprovalStageProgressDto stage)
    {
        if (!IsScoped(stage))
            return [];
        if (IsAdministrator(user))
            return stage.ScopeWarehouses;
        var assigned = user.GetWarehouseCodes();
        return stage.ScopeWarehouses
            .Where(code => assigned.Contains(code, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    /// <remarks>
    /// A scoped stage with no warehouses to scope to — a document whose end is unknown — falls back to
    /// its role alone rather than to nobody, so it can still be decided.
    /// </remarks>
    private static bool IsScoped(ApprovalStageProgressDto stage)
        => stage.WarehouseScope != ApprovalWarehouseScopes.Any && stage.ScopeWarehouses.Count > 0;

    private static bool IsAdministrator(User user)
        => string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="user"/> can still move <paramref name="stage"/> on: on a per-warehouse
    /// stage, only someone covering a warehouse still waiting is worth asking.
    /// </summary>
    public static bool CanAdvance(User user, ApprovalStageProgressDto stage)
    {
        if (!IsAuthorizer(user, stage))
            return false;
        if (stage.WarehouseProgress.Count == 0)
            return true;
        var covered = CoveredWarehouses(user, stage);
        return stage.WarehouseProgress.Any(item =>
            item.Status == ApprovalRequestStatuses.Pending &&
            covered.Contains(item.WarehouseCode, StringComparer.OrdinalIgnoreCase));
    }

    private async Task NotifyPendingAuthorizersAsync(
        ApprovalRequestEntity request,
        List<ApprovalStageSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        try
        {
            // The receiving-owner stage is asked last, and not of anyone already asked about another
            // stage of this request: their approval there counts as the owner's too
            // (RecordOwnerAgreement), so a second alert would only ask the same question twice.
            var receivingOwnerStageIds = snapshots
                .Where(snapshot => snapshot.FollowsReceivingOwnerRule)
                .Select(snapshot => snapshot.Id)
                .ToHashSet();
            var pendingStages = BuildProgress(request, snapshots)
                .Where(stage => stage.Status == ApprovalRequestStatuses.Pending)
                .OrderBy(stage => receivingOwnerStageIds.Contains(stage.StageId))
                .ToList();
            var askedAboutAnotherStage = new HashSet<Guid>();
            foreach (var stage in pendingStages)
            {
                var isReceivingOwnerStage = receivingOwnerStageIds.Contains(stage.StageId);
                var decidedUserIds = stage.Decisions.Select(item => item.AuthorizerUserId).ToHashSet();
                var authorizerUserIds = stage.AuthorizerUserIds;
                var authorizerRoles = stage.AuthorizerRoles;
                var recipients = await context.Users.AsNoTracking()
                    .Where(user => user.IsActive &&
                                   (authorizerUserIds.Contains(user.Id) || authorizerRoles.Contains(user.Role)))
                    .ToListAsync(cancellationToken);

                var isDirectTransfer = request.DocumentType == ApprovalDocumentTypes.InventoryTransfer;
                var label = DocumentLabel(request.DocumentType);

                // Nobody is asked for a decision they are not allowed to give — which, on a
                // warehouse-scoped stage, includes every stock officer not assigned to the warehouse.
                foreach (var recipient in recipients
                             .Where(user => !decidedUserIds.Contains(user.Id) &&
                                            MayApprove(request, user.Id) &&
                                            CanAdvance(user, stage))
                             .DistinctBy(user => user.Id))
                {
                    if (isReceivingOwnerStage && askedAboutAnotherStage.Contains(recipient.Id))
                        continue;
                    if (!isReceivingOwnerStage)
                        askedAboutAnotherStage.Add(recipient.Id);

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
                        ActionUrl = DocumentActionUrl(request),
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

    /// <summary>
    /// Tells whoever raised the document that the approval process has finished with it.
    /// </summary>
    private Task NotifyOriginatorOfDecisionAsync(
        ApprovalRequestEntity request,
        User authorizer,
        ApprovalDecisionEntity decision,
        CancellationToken cancellationToken)
    {
        var approved = request.Status == ApprovalRequestStatuses.Approved;
        var label = DocumentLabel(request.DocumentType);
        var reference = request.DocumentNumber ?? request.DocumentKey;
        var remarks = string.IsNullOrWhiteSpace(decision.Remarks) ? null : $" Remarks: {decision.Remarks}";

        return NotifyOriginatorAsync(
            request,
            approved ? ApprovalOutcomes.Approved : ApprovalOutcomes.NotApproved,
            approved
                ? $"{label} #{reference} approved"
                : $"{label} #{reference} was not approved",
            approved
                ? $"{decision.StageName} approved by {authorizer.Username}. " +
                  $"{request.FromWarehouse} to {request.ToWarehouse}.{remarks}"
                : $"{decision.StageName} rejected by {authorizer.Username}. " +
                  $"{request.FromWarehouse} to {request.ToWarehouse}.{remarks}",
            approved ? "Success" : "Warning",
            authorizer.Id,
            null,
            cancellationToken);
    }

    /// <summary>
    /// Tells whoever raised the document that it has become a real SAP transfer — the outcome they
    /// were actually waiting for, and the point at which their approval alert stops being useful.
    /// </summary>
    private async Task NotifyOriginatorOfGenerationAsync(
        ApprovalRequestEntity request,
        Guid generatedByUserId,
        int transferDocNum,
        CancellationToken cancellationToken)
    {
        var label = DocumentLabel(request.DocumentType);
        var reference = request.DocumentNumber ?? request.DocumentKey;

        // Approving and converting is one action to the authorizer who does it, so "approved" and
        // "posted" would land together and say the same thing twice. The later, fuller answer
        // supersedes the earlier one — cleared first, or the sweep would take the new one with it.
        await MarkOriginatorOutcomesReadAsync(request, cancellationToken);

        await NotifyOriginatorAsync(
            request,
            ApprovalOutcomes.Generated,
            $"{label} #{reference} posted as transfer #{transferDocNum}",
            $"Approved and posted to SAP as inventory transfer #{transferDocNum}. " +
            $"{request.FromWarehouse} to {request.ToWarehouse}.",
            "Success",
            generatedByUserId,
            transferDocNum.ToString(),
            cancellationToken);
    }

    /// <summary>
    /// The approval flow only ever spoke to authorizers, so the person who raised a transfer had no
    /// way to learn what happened to it except by reopening the transfers page and looking. These
    /// are written under their own entity type: the read sweeps above clear the "needs approval"
    /// alerts, and an outcome is not one of those — it is the answer.
    /// </summary>
    private async Task NotifyOriginatorAsync(
        ApprovalRequestEntity request,
        string outcome,
        string title,
        string message,
        string type,
        Guid? actedByUserId,
        string? generatedDocNum,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.OriginatorUserId is not { } originatorUserId)
                return;

            // Someone acting on a document they raised themselves already knows the outcome.
            if (actedByUserId == originatorUserId)
                return;

            var originator = await context.Users.AsNoTracking()
                .FirstOrDefaultAsync(user => user.Id == originatorUserId && user.IsActive, cancellationToken);
            if (originator is null)
                return;

            var entityId = ApprovalOutcomeNotificationEntityId(request.DocumentType, request.DocumentKey, outcome);
            var exists = await context.Notifications.AsNoTracking().AnyAsync(item =>
                item.Category == "TransferApproval" &&
                item.EntityType == ApprovalOutcomeEntityType &&
                item.EntityId == entityId &&
                item.TargetUsername == originator.Username,
                cancellationToken);
            if (exists)
                return;

            var data = new Dictionary<string, string>
            {
                ["documentType"] = request.DocumentType,
                ["requestDocEntry"] = request.DocumentKey,
                ["requestDocNum"] = request.DocumentNumber ?? request.DocumentKey,
                ["approvalRequestId"] = request.Id.ToString(),
                ["outcome"] = outcome,
                ["documentLabel"] = DocumentLabel(request.DocumentType)
            };
            if (generatedDocNum is not null)
                data["generatedDocNum"] = generatedDocNum;

            await notificationService.CreateNotificationAsync(new CreateNotificationRequest
            {
                Title = title,
                Message = message,
                Type = type,
                Category = "TransferApproval",
                EntityType = ApprovalOutcomeEntityType,
                EntityId = entityId,
                ActionUrl = DocumentActionUrl(request),
                TargetUserId = originator.Id,
                TargetUsername = originator.Username,
                Data = data
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            // Telling the originator is not worth failing the decision that has already been
            // recorded, exactly as notifying the authorizers is not.
            logger.LogWarning(ex,
                "Failed to notify the originator of {DocumentType} {DocumentKey} that it was {Outcome}",
                request.DocumentType, request.DocumentKey, outcome);
        }
    }

    private Task MarkOriginatorOutcomesReadAsync(ApprovalRequestEntity request, CancellationToken cancellationToken)
    {
        var prefix = ApprovalNotificationEntityPrefix(request.DocumentType, request.DocumentKey);
        return context.Notifications
            .Where(item => item.Category == "TransferApproval" &&
                           item.EntityType == ApprovalOutcomeEntityType &&
                           item.EntityId != null &&
                           item.EntityId.StartsWith(prefix) &&
                           !item.IsRead)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.IsRead, true)
                .SetProperty(item => item.ReadAt, DateTime.UtcNow), cancellationToken);
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

    // Shares the prefix with the per-stage ids, but never collides with one: a stage id is 32 hex
    // characters and an outcome is a word.
    private static string ApprovalOutcomeNotificationEntityId(string documentType, string documentKey, string outcome)
        => $"{ApprovalNotificationEntityPrefix(documentType, documentKey)}{outcome}";

    private static string DocumentLabel(string documentType) => documentType switch
    {
        ApprovalDocumentTypes.InventoryTransfer => "Inventory transfer",
        ApprovalDocumentTypes.InventoryTransferRequestEdit => "Transfer request edit",
        _ => "Transfer request"
    };

    /// <summary>
    /// Where the notification for a document should land. The transfers page opens the record named
    /// by the query string, so this is a deep link for the two document types it can open on its
    /// own. A held edit is reached through the request it amends, and the approval carries the
    /// edit's id rather than that request's DocEntry, so it links to the page unfiltered rather
    /// than to a parameter that would not resolve.
    /// </summary>
    private static string DocumentActionUrl(ApprovalRequestEntity request) => request.DocumentType switch
    {
        ApprovalDocumentTypes.InventoryTransfer => $"/inventory-transfers?pendingTransferId={request.DocumentKey}",
        ApprovalDocumentTypes.InventoryTransferRequest => $"/inventory-transfers?requestDocEntry={request.DocumentKey}",
        _ => "/inventory-transfers"
    };

    private static class ApprovalOutcomes
    {
        public const string Approved = "approved";
        public const string NotApproved = "not-approved";
        public const string Generated = "generated";
    }

    private static ApprovalStageDefinitionEntity NewStage(Guid id, string name, string description, string role) => new()
    { Id = id, Name = name, Description = description, AuthorizerRolesJson = Serialize([role]) };
    private static ApprovalTemplateDefinitionEntity NewTemplate(Guid id, string name, string documentType, int priority, IEnumerable<string> roles, IEnumerable<Guid> stages) => new()
    { Id = id, Name = name, Description = name, DocumentType = documentType, Priority = priority, OriginatorRolesJson = Serialize(roles), StageIdsJson = Serialize(stages) };
    private static ApprovalStageSnapshot ToSnapshot(ApprovalStageDefinitionEntity stage, ApprovalDocumentContext document) => new()
    {
        Id = stage.Id, Name = stage.Name, Description = stage.Description, ApprovalsRequired = stage.ApprovalsRequired, RejectionsRequired = stage.RejectionsRequired,
        AuthorizerUserIds = Deserialize<Guid>(stage.AuthorizerUserIdsJson), AuthorizerRoles = Deserialize<string>(stage.AuthorizerRolesJson),
        WarehouseScope = ApprovalWarehouseScopes.Normalize(stage.WarehouseScope), ApprovalPerWarehouse = stage.ApprovalPerWarehouse,
        ScopeWarehouses = ScopeWarehousesFor(stage.WarehouseScope, document)
    };
    private static List<string> ScopeWarehousesFor(string? scope, ApprovalDocumentContext document) => ApprovalWarehouseScopes.Normalize(scope) switch
    {
        ApprovalWarehouseScopes.Source => [.. document.SourceWarehouses],
        ApprovalWarehouseScopes.Destination => [.. document.DestinationWarehouses],
        _ => []
    };
    private static ApprovalStageDefinitionDto ToDto(ApprovalStageDefinitionEntity stage) => new()
    { Id = stage.Id, Name = stage.Name, Description = stage.Description, ApprovalsRequired = stage.ApprovalsRequired, RejectionsRequired = stage.RejectionsRequired, AuthorizerUserIds = Deserialize<Guid>(stage.AuthorizerUserIdsJson), AuthorizerRoles = Deserialize<string>(stage.AuthorizerRolesJson), WarehouseScope = ApprovalWarehouseScopes.Normalize(stage.WarehouseScope), ApprovalPerWarehouse = stage.ApprovalPerWarehouse, IsActive = stage.IsActive };
    private static ApprovalTemplateDefinitionDto ToDto(ApprovalTemplateDefinitionEntity template) => new()
    { Id = template.Id, Name = template.Name, Description = template.Description, DocumentType = template.DocumentType, OriginatorUserIds = Deserialize<Guid>(template.OriginatorUserIdsJson), OriginatorRoles = Deserialize<string>(template.OriginatorRolesJson), StageIds = Deserialize<Guid>(template.StageIdsJson), FromWarehouses = Deserialize<string>(template.FromWarehousesJson), ToWarehouses = Deserialize<string>(template.ToWarehousesJson), Priority = template.Priority, IsActive = template.IsActive };
    private static string Serialize<T>(IEnumerable<T> values) => JsonSerializer.Serialize(values, JsonOptions);
    private static List<T> Deserialize<T>(string? json) { try { return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? []; } catch { return []; } }
    private static List<string> NormalizeCodes(IEnumerable<string>? codes) => (codes ?? [])
        .Where(code => !string.IsNullOrWhiteSpace(code))
        .Select(code => code.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The request's display route: one code, or several joined, within the column's 400 characters.</summary>
    private static string? RouteLabel(IReadOnlyList<string> codes, string? header)
    {
        var label = codes.Count > 0 ? string.Join(", ", codes) : header;
        return label is { Length: > 400 } ? label[..400] : label;
    }
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

        // Snapshots written before stages could be scoped read back as Any with no warehouses, which
        // is exactly how they were decided.
        public string WarehouseScope { get; set; } = ApprovalWarehouseScopes.Any;
        public bool ApprovalPerWarehouse { get; set; }

        /// <summary>The document's warehouses on the stage's end, fixed when the approval opened or was reopened.</summary>
        public List<string> ScopeWarehouses { get; set; } = [];

        /// <summary>
        /// Aimed by <see cref="ReceivingWarehouseOwnerRule"/>: vans left out of the scope, and the
        /// requester's own warehouses counted as approved. False on every snapshot written before it.
        /// </summary>
        public bool FollowsReceivingOwnerRule { get; set; }

        /// <summary>Scope warehouses the requester runs, approved by their raising the request.</summary>
        public List<string> ConsentedWarehouses { get; set; } = [];
    }
}
