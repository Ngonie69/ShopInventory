using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Commands.GenerateDocument;

public sealed record GenerateDocumentCommand(
    GenerateDocumentRequest Request,
    Guid UserId
) : IRequest<ErrorOr<GenerateDocumentResponseDto>>;
