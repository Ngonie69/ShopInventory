using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Queries.GetTemplateById;

public sealed record GetTemplateByIdQuery(int Id) : IRequest<ErrorOr<DocumentTemplateDto>>;
