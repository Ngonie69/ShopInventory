using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Commands.CreateEmailTemplate;

public sealed record CreateEmailTemplateCommand(
    UpsertEmailTemplateRequest Request
) : IRequest<ErrorOr<EmailTemplateDto>>;
