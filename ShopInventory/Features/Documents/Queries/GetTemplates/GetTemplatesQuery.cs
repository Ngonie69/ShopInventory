using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Queries.GetTemplates;

public sealed record GetTemplatesQuery(
    string? DocumentType,
    bool? ActiveOnly,
    int Page,
    int PageSize
) : IRequest<ErrorOr<DocumentTemplateListResponseDto>>;
