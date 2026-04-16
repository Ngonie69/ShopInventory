using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.CreateSignature;

public sealed class CreateSignatureHandler(
    IDocumentService documentService,
    ILogger<CreateSignatureHandler> logger
) : IRequestHandler<CreateSignatureCommand, ErrorOr<DocumentSignatureDto>>
{
    public async Task<ErrorOr<DocumentSignatureDto>> Handle(
        CreateSignatureCommand command,
        CancellationToken cancellationToken)
    {
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
