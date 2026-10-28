using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Documents;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.CreateSignature;

public sealed class CreateSignatureHandler(
    DocumentAttachmentAccessService accessService,
    IDocumentService documentService,
    ILogger<CreateSignatureHandler> logger
) : IRequestHandler<CreateSignatureCommand, ErrorOr<DocumentSignatureDto>>
{
    public async Task<ErrorOr<DocumentSignatureDto>> Handle(
        CreateSignatureCommand command,
        CancellationToken cancellationToken)
    {
        // The document is rendered from the entity, so the caller must be able to read that entity.
        var access = await accessService.AuthorizeEntityAccessAsync(
            command.Request.DocumentType, command.Request.DocumentId, isWriteOperation: false, cancellationToken);
        if (access.IsError)
        {
            return access.Errors;
        }

        var signer = await accessService.RefuseReadOnlyManagerAsync(cancellationToken);
        if (signer.IsError)
        {
            return signer.Errors;
        }

        try
        {
            var signature = await documentService.CreateSignatureAsync(
                command.Request, command.UserId, command.IpAddress, command.DeviceInfo, cancellationToken);
            return signature;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating signature");
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
