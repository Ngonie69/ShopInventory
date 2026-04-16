using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Queries.GetAttachments;

public sealed record GetAttachmentsQuery(
    string EntityType,
    int EntityId
) : IRequest<ErrorOr<DocumentAttachmentListResponseDto>>;
