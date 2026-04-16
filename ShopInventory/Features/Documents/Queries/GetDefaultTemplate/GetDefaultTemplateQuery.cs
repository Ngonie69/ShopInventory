using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Queries.GetDefaultTemplate;

public sealed record GetDefaultTemplateQuery(string DocumentType) : IRequest<ErrorOr<DocumentTemplateDto>>;
