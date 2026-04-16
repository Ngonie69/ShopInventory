using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Commands.CreateTemplate;

public sealed record CreateTemplateCommand(
    UpsertDocumentTemplateRequest Request,
    Guid UserId
) : IRequest<ErrorOr<DocumentTemplateDto>>;
