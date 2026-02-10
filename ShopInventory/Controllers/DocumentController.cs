using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Services;
using System.Security.Claims;

namespace ShopInventory.Controllers;

/// <summary>
/// Document management controller for templates, generation, attachments, and signatures
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DocumentController : ControllerBase
{
    private readonly IDocumentService _documentService;
    private readonly ILogger<DocumentController> _logger;

    public DocumentController(IDocumentService documentService, ILogger<DocumentController> logger)
    {
        _documentService = documentService;
        _logger = logger;
    }

    #region Document Templates

    /// <summary>
    /// Get all document templates
    /// </summary>
    [HttpGet("templates")]
    [ProducesResponseType(typeof(DocumentTemplateListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTemplates(
        [FromQuery] string? documentType = null,
        [FromQuery] bool? activeOnly = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _documentService.GetAllTemplatesAsync(documentType, activeOnly, page, pageSize, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Get template by ID
    /// </summary>
    [HttpGet("templates/{id}")]
    [ProducesResponseType(typeof(DocumentTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTemplateById(int id, CancellationToken cancellationToken = default)
    {
        var template = await _documentService.GetTemplateByIdAsync(id, cancellationToken);
        if (template == null)
        {
            return NotFound(new ErrorResponseDto { Message = "Template not found" });
        }
        return Ok(template);
    }

    /// <summary>
    /// Get default template for document type
    /// </summary>
    [HttpGet("templates/default/{documentType}")]
    [ProducesResponseType(typeof(DocumentTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDefaultTemplate(string documentType, CancellationToken cancellationToken = default)
    {
        var template = await _documentService.GetDefaultTemplateAsync(documentType, cancellationToken);
        if (template == null)
        {
            return NotFound(new ErrorResponseDto { Message = "Default template not found for this document type" });
        }
        return Ok(template);
    }

    /// <summary>
    /// Create new document template
    /// </summary>
    [HttpPost("templates")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(DocumentTemplateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateTemplate(
        [FromBody] UpsertDocumentTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        var template = await _documentService.CreateTemplateAsync(request, userId, cancellationToken);
        return CreatedAtAction(nameof(GetTemplateById), new { id = template.Id }, template);
    }

    /// <summary>
    /// Update document template
    /// </summary>
    [HttpPut("templates/{id}")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(DocumentTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateTemplate(
        int id,
        [FromBody] UpsertDocumentTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var template = await _documentService.UpdateTemplateAsync(id, request, cancellationToken);
            return Ok(template);
        }
        catch (Exception ex)
        {
            return NotFound(new ErrorResponseDto { Message = ex.Message });
        }
    }

    /// <summary>
    /// Delete document template
    /// </summary>
    [HttpDelete("templates/{id}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTemplate(int id, CancellationToken cancellationToken = default)
    {
        var result = await _documentService.DeleteTemplateAsync(id, cancellationToken);
        if (!result)
        {
            return NotFound(new ErrorResponseDto { Message = "Template not found" });
        }
        return NoContent();
    }

    /// <summary>
    /// Set template as default for document type
    /// </summary>
    [HttpPost("templates/{id}/set-default")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefaultTemplate(int id, CancellationToken cancellationToken = default)
    {
        var result = await _documentService.SetDefaultTemplateAsync(id, cancellationToken);
        if (!result)
        {
            return NotFound(new ErrorResponseDto { Message = "Template not found" });
        }
        return Ok(new { message = "Template set as default successfully" });
    }

    /// <summary>
    /// Get available placeholders for document type
    /// </summary>
    [HttpGet("templates/placeholders/{documentType}")]
    [ProducesResponseType(typeof(TemplatePlaceholdersDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlaceholders(string documentType, CancellationToken cancellationToken = default)
    {
        var placeholders = await _documentService.GetPlaceholdersAsync(documentType, cancellationToken);
        return Ok(placeholders);
    }

    #endregion

    #region Document Generation

    /// <summary>
    /// Generate document (PDF)
    /// </summary>
    [HttpPost("generate")]
    [ProducesResponseType(typeof(GenerateDocumentResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GenerateDocument(
        [FromBody] GenerateDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        var result = await _documentService.GenerateDocumentAsync(request, userId, cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new ErrorResponseDto { Message = result.Message ?? "Document generation failed" });
        }

        return Ok(result);
    }

    /// <summary>
    /// Generate and download document
    /// </summary>
    [HttpPost("generate/download")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GenerateAndDownloadDocument(
        [FromBody] GenerateDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        var result = await _documentService.GenerateDocumentAsync(request, userId, cancellationToken);

        if (!result.Success || result.FileContent == null)
        {
            return BadRequest(new ErrorResponseDto { Message = result.Message ?? "Document download failed" });
        }

        return File(result.FileContent, "application/pdf", result.FileName);
    }

    /// <summary>
    /// Generate and email document
    /// </summary>
    [HttpPost("email")]
    [ProducesResponseType(typeof(GenerateDocumentResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EmailDocument(
        [FromBody] EmailDocumentRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        var result = await _documentService.EmailDocumentAsync(request, userId, cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new ErrorResponseDto { Message = result.Message ?? "Email sending failed" });
        }

        return Ok(result);
    }

    #endregion

    #region Document Attachments

    /// <summary>
    /// Upload document attachment
    /// </summary>
    [HttpPost("attachments")]
    [ProducesResponseType(typeof(DocumentAttachmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadAttachment(
        [FromForm] string entityType,
        [FromForm] int entityId,
        [FromForm] string? description,
        [FromForm] bool includeInEmail,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new ErrorResponseDto { Message = "No file uploaded" });
        }

        var request = new UploadAttachmentRequest
        {
            EntityType = entityType,
            EntityId = entityId,
            Description = description,
            IncludeInEmail = includeInEmail
        };

        var userId = GetUserId();
        using var stream = file.OpenReadStream();
        var attachment = await _documentService.UploadAttachmentAsync(
            request, stream, file.FileName, file.ContentType, userId, cancellationToken);

        return CreatedAtAction(nameof(DownloadAttachment), new { id = attachment.Id }, attachment);
    }

    /// <summary>
    /// Get attachments for entity
    /// </summary>
    [HttpGet("attachments")]
    [ProducesResponseType(typeof(DocumentAttachmentListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAttachments(
        [FromQuery] string entityType,
        [FromQuery] int entityId,
        CancellationToken cancellationToken = default)
    {
        var result = await _documentService.GetAttachmentsAsync(entityType, entityId, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Download attachment
    /// </summary>
    [HttpGet("attachments/{id}/download")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAttachment(int id, CancellationToken cancellationToken = default)
    {
        var (stream, fileName, mimeType) = await _documentService.DownloadAttachmentAsync(id, cancellationToken);

        if (stream == null)
        {
            return NotFound(new ErrorResponseDto { Message = "Attachment not found" });
        }

        return File(stream, mimeType ?? "application/octet-stream", fileName);
    }

    /// <summary>
    /// Delete attachment
    /// </summary>
    [HttpDelete("attachments/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAttachment(int id, CancellationToken cancellationToken = default)
    {
        var result = await _documentService.DeleteAttachmentAsync(id, cancellationToken);
        if (!result)
        {
            return NotFound(new ErrorResponseDto { Message = "Attachment not found" });
        }
        return NoContent();
    }

    #endregion

    #region Document History

    /// <summary>
    /// Get document generation history
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(DocumentHistoryListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDocumentHistory(
        [FromQuery] string? documentType = null,
        [FromQuery] int? entityId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await _documentService.GetDocumentHistoryAsync(documentType, entityId, page, pageSize, cancellationToken);
        return Ok(result);
    }

    #endregion

    #region Digital Signatures

    /// <summary>
    /// Create digital signature
    /// </summary>
    [HttpPost("signatures")]
    [ProducesResponseType(typeof(DocumentSignatureDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateSignature(
        [FromBody] CreateSignatureRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = User.Identity?.IsAuthenticated == true ? (Guid?)GetUserId() : null;
        var ipAddress = GetClientIpAddress();
        var deviceInfo = Request.Headers["User-Agent"].ToString();

        var signature = await _documentService.CreateSignatureAsync(
            request, userId, ipAddress, deviceInfo, cancellationToken);

        return CreatedAtAction(nameof(GetSignatures),
            new { documentType = request.DocumentType, documentId = request.DocumentId },
            signature);
    }

    /// <summary>
    /// Get signatures for document
    /// </summary>
    [HttpGet("signatures")]
    [ProducesResponseType(typeof(DocumentSignatureListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSignatures(
        [FromQuery] string documentType,
        [FromQuery] int documentId,
        CancellationToken cancellationToken = default)
    {
        var result = await _documentService.GetSignaturesAsync(documentType, documentId, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Verify signature
    /// </summary>
    [HttpPost("signatures/{id}/verify")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VerifySignature(int id, CancellationToken cancellationToken = default)
    {
        var result = await _documentService.VerifySignatureAsync(id, cancellationToken);
        if (!result)
        {
            return NotFound(new ErrorResponseDto { Message = "Signature not found" });
        }
        return Ok(new { message = "Signature verified successfully" });
    }

    #endregion

    #region Email Templates

    /// <summary>
    /// Get all email templates
    /// </summary>
    [HttpGet("email-templates")]
    [ProducesResponseType(typeof(EmailTemplateListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEmailTemplates(
        [FromQuery] bool? activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _documentService.GetAllEmailTemplatesAsync(activeOnly, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Get email template by code
    /// </summary>
    [HttpGet("email-templates/{templateCode}")]
    [ProducesResponseType(typeof(EmailTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEmailTemplateByCode(string templateCode, CancellationToken cancellationToken = default)
    {
        var template = await _documentService.GetEmailTemplateByCodeAsync(templateCode, cancellationToken);
        if (template == null)
        {
            return NotFound(new ErrorResponseDto { Message = "Email template not found" });
        }
        return Ok(template);
    }

    /// <summary>
    /// Create email template
    /// </summary>
    [HttpPost("email-templates")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(EmailTemplateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateEmailTemplate(
        [FromBody] UpsertEmailTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        var template = await _documentService.CreateEmailTemplateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetEmailTemplateByCode), new { templateCode = template.TemplateCode }, template);
    }

    /// <summary>
    /// Update email template
    /// </summary>
    [HttpPut("email-templates/{id}")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(EmailTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateEmailTemplate(
        int id,
        [FromBody] UpsertEmailTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var template = await _documentService.UpdateEmailTemplateAsync(id, request, cancellationToken);
            return Ok(template);
        }
        catch (Exception ex)
        {
            return NotFound(new ErrorResponseDto { Message = ex.Message });
        }
    }

    #endregion

    #region Helper Methods

    private Guid GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.Parse(userIdClaim ?? throw new UnauthorizedAccessException("User ID not found"));
    }

    private string GetClientIpAddress()
    {
        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    }

    #endregion
}
