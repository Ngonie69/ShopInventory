using System.Security.Claims;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents;

public sealed class DocumentAttachmentAccessService(
    ApplicationDbContext context,
    IHttpContextAccessor httpContextAccessor,
    IUserManagementService userManagementService,
    IDocumentService documentService,
    ILogger<DocumentAttachmentAccessService> logger)
{
    private static readonly HashSet<string> InvoiceAttachmentRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Admin",
        "Cashier",
        "PodOperator",
        "Operator",
        "Driver",
        "SalesRep"
    };

    private static readonly HashSet<string> ExternalPurchaseOrderReadRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Admin",
        "Manager",
        "Cashier",
        "Merchandiser",
        "SalesRep",
        "MerchandiserPurchaseOrderViewer"
    };

    public async Task<ErrorOr<DocumentAttachmentEntity>> AuthorizeAttachmentAccessAsync(
        int attachmentId,
        bool isWriteOperation,
        CancellationToken cancellationToken)
    {
        var attachment = await context.DocumentAttachments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);

        if (attachment is null)
        {
            return Errors.Document.AttachmentNotFound(attachmentId);
        }

        var accessResult = await EnsureEntityAccessInternalAsync(
            attachment.EntityType,
            attachment.EntityId,
            attachment.ExternalReference,
            attachment.UploadedByUserId,
            isWriteOperation,
            cancellationToken);

        return accessResult.Match<ErrorOr<DocumentAttachmentEntity>>(
            _ => attachment,
            errors => errors);
    }

    public Task<ErrorOr<bool>> AuthorizeEntityAccessAsync(
        string entityType,
        int entityId,
        bool isWriteOperation,
        CancellationToken cancellationToken)
    {
        return EnsureEntityAccessInternalAsync(entityType, entityId, null, null, isWriteOperation, cancellationToken);
    }

    private async Task<ErrorOr<bool>> EnsureEntityAccessInternalAsync(
        string entityType,
        int entityId,
        string? externalReference,
        Guid? uploadedByUserId,
        bool isWriteOperation,
        CancellationToken cancellationToken)
    {
        var currentUserResult = await ResolveCurrentUserAsync(cancellationToken);
        if (currentUserResult.IsError)
        {
            return currentUserResult.Errors;
        }

        var (userId, role, assignedSection, isApiKeyBypass) = currentUserResult.Value;
        if (isApiKeyBypass)
        {
            return true;
        }

        List<string>? cachedPermissions = null;

        async Task<IReadOnlyCollection<string>> GetPermissionsAsync()
        {
            cachedPermissions ??= await userManagementService.GetEffectivePermissionsAsync(userId);
            return cachedPermissions;
        }

        var normalizedEntityType = entityType.Trim();
        if (string.Equals(normalizedEntityType, "Invoice", StringComparison.OrdinalIgnoreCase))
        {
            return await EnsureInvoiceAccessAsync(
                entityId,
                uploadedByUserId,
                userId,
                role,
                assignedSection,
                isWriteOperation,
                cancellationToken);
        }

        if (string.Equals(normalizedEntityType, "ExternalPurchaseOrder", StringComparison.OrdinalIgnoreCase))
        {
            return await EnsureExternalPurchaseOrderAccessAsync(role, isWriteOperation, GetPermissionsAsync, cancellationToken);
        }

        if (string.Equals(normalizedEntityType, CrateTrackingConstants.AttachmentEntityTypeCrateTransaction, StringComparison.OrdinalIgnoreCase))
        {
            return await EnsureCrateTransactionAccessAsync(entityId, userId, role, isWriteOperation, cancellationToken);
        }

        if (string.Equals(normalizedEntityType, CrateTrackingConstants.AttachmentEntityTypeCratePodSubmission, StringComparison.OrdinalIgnoreCase))
        {
            return await EnsureCratePodSubmissionAccessAsync(entityId, userId, role, assignedSection, uploadedByUserId, isWriteOperation, cancellationToken);
        }

        if (string.Equals(normalizedEntityType, CrateTrackingConstants.AttachmentEntityTypeCrateGrv, StringComparison.OrdinalIgnoreCase))
        {
            return await EnsureCrateGrvAccessAsync(entityId, userId, role, isWriteOperation, cancellationToken);
        }

        if (IsRole(role, "Admin") || IsRole(role, "Manager"))
        {
            return true;
        }

        logger.LogWarning(
            "Denied {Operation} access to {EntityType}/{EntityId} for role {Role}",
            isWriteOperation ? "write" : "read",
            normalizedEntityType,
            entityId,
            role);

        return Errors.Document.AccessDenied(
            $"You do not have access to {normalizedEntityType} attachments.");
    }

    private async Task<ErrorOr<bool>> EnsureInvoiceAccessAsync(
        int entityId,
        Guid? uploadedByUserId,
        Guid userId,
        string role,
        string? assignedSection,
        bool isWriteOperation,
        CancellationToken cancellationToken)
    {
        if (!InvoiceAttachmentRoles.Contains(role))
        {
            return Errors.Document.AccessDenied("You do not have access to invoice attachments.");
        }

        if (IsRole(role, "Driver"))
        {
            if (isWriteOperation && uploadedByUserId is null)
            {
                return true;
            }

            if (uploadedByUserId == userId)
            {
                return true;
            }

            return Errors.Document.AccessDenied("Drivers can only access PODs they uploaded.");
        }

        var isScopedPodViewer = IsRole(role, "PodOperator") || IsRole(role, "Operator");

        if (isScopedPodViewer)
        {
            if (string.IsNullOrWhiteSpace(assignedSection))
            {
                return Errors.Document.AccessDenied("An assigned POD section is required to access invoice attachments.");
            }

            var scopedDocEntries = await documentService.GetScopedPodInvoiceDocEntriesAsync([entityId], assignedSection, cancellationToken);
            if (!scopedDocEntries.Contains(entityId))
            {
                return Errors.Document.AccessDenied("This invoice is outside your assigned POD section.");
            }
        }

        return true;
    }

    private async Task<ErrorOr<bool>> EnsureExternalPurchaseOrderAccessAsync(
        string role,
        bool isWriteOperation,
        Func<Task<IReadOnlyCollection<string>>> getPermissionsAsync,
        CancellationToken cancellationToken)
    {
        if (!isWriteOperation && ExternalPurchaseOrderReadRoles.Contains(role))
        {
            return true;
        }

        var permissions = await getPermissionsAsync();

        if (!isWriteOperation &&
            (permissions.Contains(Permission.ViewPurchaseOrders) || permissions.Contains(Permission.ViewReports)))
        {
            return true;
        }

        if (isWriteOperation && permissions.Contains(Permission.UploadPurchaseOrderDocuments))
        {
            return true;
        }

        return Errors.Document.AccessDenied(
            isWriteOperation
                ? "You do not have permission to modify purchase order attachments."
                : "You do not have permission to view purchase order attachments.");
    }

    private async Task<ErrorOr<bool>> EnsureCrateTransactionAccessAsync(
        int entityId,
        Guid userId,
        string role,
        bool isWriteOperation,
        CancellationToken cancellationToken)
    {
        var transactionExists = await context.CrateTransactions
            .AsNoTracking()
            .AnyAsync(t => t.Id == entityId, cancellationToken);

        if (!transactionExists)
        {
            return Errors.CrateTracking.TransactionNotFound(entityId);
        }

        if (IsRole(role, "Admin") || IsRole(role, "Manager") || IsRole(role, "Merchandiser") || IsRole(role, "PodOperator"))
        {
            return true;
        }

        return Errors.Document.AccessDenied(
            isWriteOperation
                ? "You do not have permission to modify crate transaction documents."
                : "You do not have permission to view crate transaction documents.");
    }

    private async Task<ErrorOr<bool>> EnsureCratePodSubmissionAccessAsync(
        int entityId,
        Guid userId,
        string role,
        string? assignedSection,
        Guid? uploadedByUserId,
        bool isWriteOperation,
        CancellationToken cancellationToken)
    {
        var submission = await context.CratePodSubmissions
            .AsNoTracking()
            .Include(s => s.CrateTransaction)
            .FirstOrDefaultAsync(s => s.Id == entityId, cancellationToken);

        if (submission is null)
        {
            return Errors.CrateTracking.SubmissionNotFound(entityId);
        }

        if (IsRole(role, "Admin") || IsRole(role, "Manager") || IsRole(role, "Merchandiser"))
        {
            return true;
        }

        if (!isWriteOperation && (IsRole(role, "SalesRep") || IsRole(role, "PodOperator")))
        {
            return true;
        }

        if (!isWriteOperation && IsRole(role, "Operator"))
        {
            if (string.IsNullOrWhiteSpace(assignedSection))
            {
                return Errors.Document.AccessDenied("An assigned POD section is required to view crate POD attachments.");
            }

            if (submission.CrateTransaction?.InvoiceDocEntry is not int invoiceDocEntry)
            {
                return Errors.Document.AccessDenied("This crate POD is not linked to an invoice in your assigned POD section.");
            }

            var scopedDocEntries = await documentService.GetScopedPodInvoiceDocEntriesAsync([invoiceDocEntry], assignedSection, cancellationToken);
            if (scopedDocEntries.Contains(invoiceDocEntry))
            {
                return true;
            }

            return Errors.Document.AccessDenied("This crate POD is outside your assigned POD section.");
        }

        if (IsRole(role, "Driver") && (submission.SubmittedByUserId == userId || uploadedByUserId == userId))
        {
            return true;
        }

        return Errors.Document.AccessDenied(
            isWriteOperation
                ? "You can only modify your own crate POD attachments."
                : "You can only view your own crate POD attachments.");
    }

    private async Task<ErrorOr<bool>> EnsureCrateGrvAccessAsync(
        int entityId,
        Guid userId,
        string role,
        bool isWriteOperation,
        CancellationToken cancellationToken)
    {
        var grv = await context.CrateGrvs
            .AsNoTracking()
            .Include(g => g.CrateTransaction)
                .ThenInclude(t => t.PodSubmissions)
            .FirstOrDefaultAsync(g => g.Id == entityId, cancellationToken);

        if (grv is null)
        {
            return Errors.CrateTracking.GrvNotFound(entityId);
        }

        if (IsRole(role, "Admin") || IsRole(role, "Manager") || IsRole(role, "Merchandiser") || (!isWriteOperation && IsRole(role, "SalesRep")))
        {
            return true;
        }

        if (!isWriteOperation && IsRole(role, "Driver") && grv.CrateTransaction.PodSubmissions.Any(s =>
                s.SubmissionRole == CrateTrackingConstants.SubmissionRoleDriver &&
                s.SubmittedByUserId == userId))
        {
            return true;
        }

        return Errors.Document.AccessDenied(
            isWriteOperation
                ? "You do not have permission to modify crate GRV attachments."
                : "You do not have permission to view crate GRV attachments.");
    }

    private async Task<ErrorOr<(Guid UserId, string Role, string? AssignedSection, bool IsApiKeyBypass)>> ResolveCurrentUserAsync(
        CancellationToken cancellationToken)
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return Errors.Auth.Unauthenticated;
        }

        var authMethod = user.FindFirst(ClaimTypes.AuthenticationMethod)?.Value;
        if (string.Equals(authMethod, "ApiKey", StringComparison.OrdinalIgnoreCase) &&
            (user.IsInRole("Admin") || user.IsInRole("ApiUser")))
        {
            return (Guid.Empty, "ApiKey", null, true);
        }

        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Errors.Auth.Unauthenticated;
        }

        var currentUser = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Role, u.AssignedSection, u.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (currentUser is null || !currentUser.IsActive)
        {
            return Errors.Auth.UserNotFound;
        }

        return (userId, currentUser.Role, currentUser.AssignedSection, false);
    }

    private static bool IsRole(string? actualRole, string expectedRole)
    {
        return string.Equals(actualRole, expectedRole, StringComparison.OrdinalIgnoreCase);
    }
}