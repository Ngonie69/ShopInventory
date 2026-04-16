using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.VerifySignature;

public sealed class VerifySignatureHandler(
    IDocumentService documentService,
    ILogger<VerifySignatureHandler> logger
) : IRequestHandler<VerifySignatureCommand, ErrorOr<bool>>
{
    public async Task<ErrorOr<bool>> Handle(
        VerifySignatureCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await documentService.VerifySignatureAsync(command.Id, cancellationToken);
            if (!result)
            {
                return Errors.Document.SignatureNotFound(command.Id);
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error verifying signature {SignatureId}", command.Id);
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
