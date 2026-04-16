using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Commands.EmailDocument;

public sealed record EmailDocumentCommand(
    EmailDocumentRequest Request,
    Guid UserId
) : IRequest<ErrorOr<GenerateDocumentResponseDto>>;
