using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Commands.UpdateEmailTemplate;

public sealed record UpdateEmailTemplateCommand(
    int Id,
    UpsertEmailTemplateRequest Request
) : IRequest<ErrorOr<EmailTemplateDto>>;
