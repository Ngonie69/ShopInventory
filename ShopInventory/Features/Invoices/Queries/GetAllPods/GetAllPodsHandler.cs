using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Queries.GetAllPods;

public sealed class GetAllPodsHandler(
    ApplicationDbContext context,
    IDocumentService documentService
) : IRequestHandler<GetAllPodsQuery, ErrorOr<PodAttachmentListResponseDto>>
{
    public async Task<ErrorOr<PodAttachmentListResponseDto>> Handle(
        GetAllPodsQuery request,
        CancellationToken cancellationToken)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? 20 : request.PageSize > 100 ? 100 : request.PageSize;

        // A service caller (the customer portal) has no staff user, and is already
        // constrained to its own card codes by the controller.
        var currentUser = request.UserId is { } userId
            ? await context.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Role, u.AssignedSection })
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        if (currentUser is null && request.UserId is not null)
        {
            return ShopInventory.Common.Errors.Errors.Auth.UserNotFound;
        }

        // POD operators oversee uploads across the company. Only the legacy Operator role is
        // location-scoped; this matches the crate POD list and keeps the two POD views consistent.
        var isScopedPodViewer = string.Equals(currentUser?.Role, "Operator", StringComparison.OrdinalIgnoreCase);

        if (isScopedPodViewer && string.IsNullOrWhiteSpace(currentUser!.AssignedSection))
        {
            return new PodAttachmentListResponseDto
            {
                Items = [],
                TotalCount = 0,
                Page = page,
                PageSize = pageSize,
                HasMore = false
            };
        }

        var assignedSection = isScopedPodViewer
            ? currentUser!.AssignedSection
            : null;

        var result = await documentService.GetAllPodAttachmentsAsync(
            page, pageSize, request.CardCode, cancellationToken,
            request.FromDate, request.ToDate, request.Search, request.UploadedByUserId, request.UploadedByUsername, assignedSection, request.UploadedFromLocation);

        return result;
    }
}
