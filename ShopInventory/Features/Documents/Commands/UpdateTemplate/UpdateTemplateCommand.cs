using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Commands.UpdateTemplate;

public sealed record UpdateTemplateCommand(
    int Id,
    UpsertDocumentTemplateRequest Request
) : IRequest<ErrorOr<DocumentTemplateDto>>;
