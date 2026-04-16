using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Queries.GetEmailTemplateByCode;

public sealed record GetEmailTemplateByCodeQuery(string TemplateCode) : IRequest<ErrorOr<EmailTemplateDto>>;
