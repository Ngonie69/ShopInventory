using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Documents.Commands.CreateEmailTemplate;
using ShopInventory.Features.Documents.Commands.CreateSignature;
using ShopInventory.Features.Documents.Commands.CreateTemplate;
using ShopInventory.Features.Documents.Commands.DeleteAttachment;
using ShopInventory.Features.Documents.Commands.DeleteTemplate;
using ShopInventory.Features.Documents.Commands.EmailDocument;
using ShopInventory.Features.Documents.Commands.GenerateAndDownloadDocument;
using ShopInventory.Features.Documents.Commands.GenerateDocument;
using ShopInventory.Features.Documents.Commands.SetDefaultTemplate;
using ShopInventory.Features.Documents.Commands.UpdateEmailTemplate;
using ShopInventory.Features.Documents.Commands.UpdateTemplate;
using ShopInventory.Features.Documents.Commands.UploadAttachment;
using ShopInventory.Features.Documents.Commands.VerifySignature;
using ShopInventory.Features.Documents.Queries.DownloadAttachment;
using ShopInventory.Features.Documents.Queries.GetAttachments;
using ShopInventory.Features.Documents.Queries.GetDefaultTemplate;
using ShopInventory.Features.Documents.Queries.GetDocumentHistory;
using ShopInventory.Features.Documents.Queries.GetEmailTemplateByCode;
using ShopInventory.Features.Documents.Queries.GetEmailTemplates;
using ShopInventory.Features.Documents.Queries.GetPlaceholders;
using ShopInventory.Features.Documents.Queries.GetSignatures;
using ShopInventory.Features.Documents.Queries.GetTemplateById;
using ShopInventory.Features.Documents.Queries.GetTemplates;
using System.Security.Claims;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class DocumentController(IMediator mediator) : ApiControllerBase
{
    #region Document Templates

    [HttpGet("templates")]
    public async Task<IActionResult> GetTemplates(
        [FromQuery] string? documentType = null,
        [FromQuery] bool? activeOnly = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetTemplatesQuery(documentType, activeOnly, page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("templates/{id}")]
    public async Task<IActionResult> GetTemplateById(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTemplateByIdQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("templates/default/{documentType}")]
    public async Task<IActionResult> GetDefaultTemplate(string documentType, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetDefaultTemplateQuery(documentType), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("templates")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> CreateTemplate([FromBody] UpsertDocumentTemplateRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var result = await mediator.Send(new CreateTemplateCommand(request, userId), cancellationToken);
        return result.Match(value => CreatedAtAction(nameof(GetTemplateById), new { id = value.Id }, value), errors => Problem(errors));
    }

    [HttpPut("templates/{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> UpdateTemplate(int id, [FromBody] UpsertDocumentTemplateRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateTemplateCommand(id, request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("templates/{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteTemplate(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteTemplateCommand(id), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    [HttpPost("templates/{id}/set-default")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> SetDefaultTemplate(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new SetDefaultTemplateCommand(id), cancellationToken);
        return result.Match(_ => Ok(new { message = "Template set as default successfully" }), errors => Problem(errors));
    }

    [HttpGet("templates/placeholders/{documentType}")]
    public async Task<IActionResult> GetPlaceholders(string documentType, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPlaceholdersQuery(documentType), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion

    #region Document Generation

    [HttpPost("generate")]
    public async Task<IActionResult> GenerateDocument([FromBody] GenerateDocumentRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var result = await mediator.Send(new GenerateDocumentCommand(request, userId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("generate/download")]
    public async Task<IActionResult> GenerateAndDownloadDocument([FromBody] GenerateDocumentRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var result = await mediator.Send(new GenerateAndDownloadDocumentCommand(request, userId), cancellationToken);
        return result.Match(value => File(value.FileContent, value.ContentType, value.FileName), errors => Problem(errors));
    }

    [HttpPost("email")]
    public async Task<IActionResult> EmailDocument([FromBody] EmailDocumentRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var result = await mediator.Send(new EmailDocumentCommand(request, userId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion

    #region Document Attachments

    [HttpPost("attachments")]
    public async Task<IActionResult> UploadAttachment(
        [FromForm] string entityType,
        [FromForm] int entityId,
        [FromForm] string? description,
        [FromForm] bool includeInEmail,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);

        var result = await mediator.Send(new UploadAttachmentCommand(
            entityType, entityId, description, includeInEmail,
            ms.ToArray(), file.FileName, file.ContentType, userId), cancellationToken);
        return result.Match(value => CreatedAtAction(nameof(DownloadAttachment), new { id = value.Id }, value), errors => Problem(errors));
    }

    [HttpGet("attachments")]
    public async Task<IActionResult> GetAttachments(
        [FromQuery] string entityType,
        [FromQuery] int entityId,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAttachmentsQuery(entityType, entityId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("attachments/{id}/download")]
    public async Task<IActionResult> DownloadAttachment(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DownloadAttachmentQuery(id), cancellationToken);
        return result.Match(value => File(value.Stream, value.MimeType, value.FileName), errors => Problem(errors));
    }

    [HttpDelete("attachments/{id}")]
    public async Task<IActionResult> DeleteAttachment(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteAttachmentCommand(id), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    #endregion

    #region Document History

    [HttpGet("history")]
    public async Task<IActionResult> GetDocumentHistory(
        [FromQuery] string? documentType = null,
        [FromQuery] int? entityId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetDocumentHistoryQuery(documentType, entityId, page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion

    #region Digital Signatures

    [HttpPost("signatures")]
    public async Task<IActionResult> CreateSignature([FromBody] CreateSignatureRequest request, CancellationToken cancellationToken)
    {
        Guid? userId = User.Identity?.IsAuthenticated == true
            ? Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : null
            : null;
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
        var deviceInfo = Request.Headers["User-Agent"].ToString();

        var result = await mediator.Send(new CreateSignatureCommand(request, userId, ipAddress, deviceInfo), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetSignatures), new { documentType = request.DocumentType, documentId = request.DocumentId }, value),
            errors => Problem(errors));
    }

    [HttpGet("signatures")]
    public async Task<IActionResult> GetSignatures(
        [FromQuery] string documentType,
        [FromQuery] int documentId,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSignaturesQuery(documentType, documentId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("signatures/{id}/verify")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> VerifySignature(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new VerifySignatureCommand(id), cancellationToken);
        return result.Match(_ => Ok(new { message = "Signature verified successfully" }), errors => Problem(errors));
    }

    #endregion

    #region Email Templates

    [HttpGet("email-templates")]
    public async Task<IActionResult> GetEmailTemplates(
        [FromQuery] bool? activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetEmailTemplatesQuery(activeOnly), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("email-templates/{templateCode}")]
    public async Task<IActionResult> GetEmailTemplateByCode(string templateCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetEmailTemplateByCodeQuery(templateCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("email-templates")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> CreateEmailTemplate([FromBody] UpsertEmailTemplateRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateEmailTemplateCommand(request), cancellationToken);
        return result.Match(value => CreatedAtAction(nameof(GetEmailTemplateByCode), new { templateCode = value.TemplateCode }, value), errors => Problem(errors));
    }

    [HttpPut("email-templates/{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> UpdateEmailTemplate(int id, [FromBody] UpsertEmailTemplateRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateEmailTemplateCommand(id, request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion

    #region Helper Methods

    private Guid GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.Parse(userIdClaim ?? throw new UnauthorizedAccessException("User ID not found"));
    }

    #endregion
}
