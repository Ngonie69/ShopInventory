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
        var currentUser = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == request.UserId)
            .Select(u => new { u.Role, u.AssignedSection })
            .FirstOrDefaultAsync(cancellationToken);

        if (currentUser is null)
        {
            return ShopInventory.Common.Errors.Errors.Auth.UserNotFound;
        }

        var assignedSection = string.Equals(currentUser.Role, "PodOperator", StringComparison.OrdinalIgnoreCase)
            ? currentUser.AssignedSection
            : null;

        var result = await documentService.GetAllPodAttachmentsAsync(
            page, pageSize, request.CardCode, cancellationToken,
            request.FromDate, request.ToDate, request.Search, request.UploadedByUserId, assignedSection);

        return result;
    }
}
