using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Queries.GetEmailTemplates;

public sealed record GetEmailTemplatesQuery(bool? ActiveOnly) : IRequest<ErrorOr<EmailTemplateListResponseDto>>;
