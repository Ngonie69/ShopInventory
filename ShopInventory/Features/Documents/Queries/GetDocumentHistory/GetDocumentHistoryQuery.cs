using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Queries.GetDocumentHistory;

public sealed record GetDocumentHistoryQuery(
    string? DocumentType,
    int? EntityId,
    int Page,
    int PageSize
) : IRequest<ErrorOr<DocumentHistoryListResponseDto>>;
