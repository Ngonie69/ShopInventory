using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Documents.Commands.GenerateAndDownloadDocument;

public sealed record FileDownloadResult(byte[] FileContent, string FileName, string ContentType);

public sealed record GenerateAndDownloadDocumentCommand(
    GenerateDocumentRequest Request,
    Guid UserId
) : IRequest<ErrorOr<FileDownloadResult>>;
