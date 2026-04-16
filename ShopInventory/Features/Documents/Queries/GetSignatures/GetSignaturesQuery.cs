using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Queries.GetSignatures;

public sealed record GetSignaturesQuery(
    string DocumentType,
    int DocumentId
) : IRequest<ErrorOr<DocumentSignatureListResponseDto>>;
