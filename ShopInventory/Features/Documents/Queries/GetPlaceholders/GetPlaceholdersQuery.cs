using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Queries.GetPlaceholders;

public sealed record GetPlaceholdersQuery(string DocumentType) : IRequest<ErrorOr<TemplatePlaceholdersDto>>;
