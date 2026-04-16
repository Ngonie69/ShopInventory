using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Documents.Commands.CreateTemplate;

public sealed class CreateTemplateHandler(
    IDocumentService documentService,
    ILogger<CreateTemplateHandler> logger
) : IRequestHandler<CreateTemplateCommand, ErrorOr<DocumentTemplateDto>>
{
    public async Task<ErrorOr<DocumentTemplateDto>> Handle(
        CreateTemplateCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var template = await documentService.CreateTemplateAsync(command.Request, command.UserId, cancellationToken);
            return template;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating document template");
            return Errors.Document.GenerationFailed(ex.Message);
        }
    }
}
