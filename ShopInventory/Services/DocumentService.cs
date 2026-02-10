using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using System.Text;
using System.Text.RegularExpressions;

namespace ShopInventory.Services;

/// <summary>
/// Service interface for document management operations
/// </summary>
public interface IDocumentService
{
    // Template Management
    Task<DocumentTemplateDto?> GetTemplateByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<DocumentTemplateDto?> GetDefaultTemplateAsync(string documentType, CancellationToken cancellationToken = default);
    Task<DocumentTemplateListResponseDto> GetAllTemplatesAsync(string? documentType = null, bool? activeOnly = null, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);
    Task<DocumentTemplateDto> CreateTemplateAsync(UpsertDocumentTemplateRequest request, Guid userId, CancellationToken cancellationToken = default);
    Task<DocumentTemplateDto> UpdateTemplateAsync(int id, UpsertDocumentTemplateRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteTemplateAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> SetDefaultTemplateAsync(int id, CancellationToken cancellationToken = default);
    Task<TemplatePlaceholdersDto> GetPlaceholdersAsync(string documentType, CancellationToken cancellationToken = default);

    // Document Generation
    Task<GenerateDocumentResponseDto> GenerateDocumentAsync(GenerateDocumentRequest request, Guid userId, CancellationToken cancellationToken = default);
    Task<GenerateDocumentResponseDto> EmailDocumentAsync(EmailDocumentRequest request, Guid userId, CancellationToken cancellationToken = default);

    // Document Attachments
    Task<DocumentAttachmentDto> UploadAttachmentAsync(UploadAttachmentRequest request, Stream fileStream, string fileName, string mimeType, Guid userId, CancellationToken cancellationToken = default);
    Task<DocumentAttachmentListResponseDto> GetAttachmentsAsync(string entityType, int entityId, CancellationToken cancellationToken = default);
    Task<(Stream? stream, string? fileName, string? mimeType)> DownloadAttachmentAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> DeleteAttachmentAsync(int id, CancellationToken cancellationToken = default);

    // Document History
    Task<DocumentHistoryListResponseDto> GetDocumentHistoryAsync(string? documentType = null, int? entityId = null, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    // Digital Signatures
    Task<DocumentSignatureDto> CreateSignatureAsync(CreateSignatureRequest request, Guid? userId, string? ipAddress, string? deviceInfo, CancellationToken cancellationToken = default);
    Task<DocumentSignatureListResponseDto> GetSignaturesAsync(string documentType, int documentId, CancellationToken cancellationToken = default);
    Task<bool> VerifySignatureAsync(int signatureId, CancellationToken cancellationToken = default);

    // Email Templates
    Task<EmailTemplateDto?> GetEmailTemplateByCodeAsync(string templateCode, CancellationToken cancellationToken = default);
    Task<EmailTemplateListResponseDto> GetAllEmailTemplatesAsync(bool? activeOnly = null, CancellationToken cancellationToken = default);
    Task<EmailTemplateDto> CreateEmailTemplateAsync(UpsertEmailTemplateRequest request, CancellationToken cancellationToken = default);
    Task<EmailTemplateDto> UpdateEmailTemplateAsync(int id, UpsertEmailTemplateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Document service implementation
/// </summary>
public class DocumentService : IDocumentService
{
    private readonly ApplicationDbContext _context;
    private readonly IEmailService _emailService;
    private readonly ILogger<DocumentService> _logger;
    private readonly string _uploadPath;

    public DocumentService(
        ApplicationDbContext context,
        IEmailService emailService,
        ILogger<DocumentService> logger,
        IConfiguration configuration)
    {
        _context = context;
        _emailService = emailService;
        _logger = logger;
        _uploadPath = configuration["FileStorage:UploadPath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");

        // Ensure upload directory exists
        if (!Directory.Exists(_uploadPath))
        {
            Directory.CreateDirectory(_uploadPath);
        }
    }

    #region Template Management

    public async Task<DocumentTemplateDto?> GetTemplateByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var template = await _context.Set<DocumentTemplateEntity>()
            .Include(t => t.CreatedByUser)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        return template == null ? null : MapToDto(template);
    }

    public async Task<DocumentTemplateDto?> GetDefaultTemplateAsync(string documentType, CancellationToken cancellationToken = default)
    {
        var template = await _context.Set<DocumentTemplateEntity>()
            .Include(t => t.CreatedByUser)
            .FirstOrDefaultAsync(t => t.DocumentType == documentType && t.IsDefault && t.IsActive, cancellationToken);

        return template == null ? null : MapToDto(template);
    }

    public async Task<DocumentTemplateListResponseDto> GetAllTemplatesAsync(string? documentType = null, bool? activeOnly = null, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var query = _context.Set<DocumentTemplateEntity>()
            .Include(t => t.CreatedByUser)
            .AsQueryable();

        if (!string.IsNullOrEmpty(documentType))
        {
            query = query.Where(t => t.DocumentType == documentType);
        }

        if (activeOnly == true)
        {
            query = query.Where(t => t.IsActive);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var templates = await query
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new DocumentTemplateListResponseDto
        {
            Templates = templates.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<DocumentTemplateDto> CreateTemplateAsync(UpsertDocumentTemplateRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var template = new DocumentTemplateEntity
        {
            Name = request.Name,
            DocumentType = request.DocumentType,
            HtmlContent = request.HtmlContent,
            CssStyles = request.CssStyles,
            HeaderContent = request.HeaderContent,
            FooterContent = request.FooterContent,
            PaperSize = request.PaperSize,
            Orientation = request.Orientation,
            IsDefault = request.IsDefault,
            IsActive = request.IsActive,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        };

        // If setting as default, unset other defaults for this document type
        if (request.IsDefault)
        {
            await UnsetDefaultTemplatesAsync(request.DocumentType, null, cancellationToken);
        }

        _context.Set<DocumentTemplateEntity>().Add(template);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(template);
    }

    public async Task<DocumentTemplateDto> UpdateTemplateAsync(int id, UpsertDocumentTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var template = await _context.Set<DocumentTemplateEntity>()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (template == null)
        {
            throw new Exception($"Template with ID {id} not found");
        }

        template.Name = request.Name;
        template.DocumentType = request.DocumentType;
        template.HtmlContent = request.HtmlContent;
        template.CssStyles = request.CssStyles;
        template.HeaderContent = request.HeaderContent;
        template.FooterContent = request.FooterContent;
        template.PaperSize = request.PaperSize;
        template.Orientation = request.Orientation;
        template.IsDefault = request.IsDefault;
        template.IsActive = request.IsActive;
        template.UpdatedAt = DateTime.UtcNow;

        // If setting as default, unset other defaults
        if (request.IsDefault)
        {
            await UnsetDefaultTemplatesAsync(request.DocumentType, id, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(template);
    }

    public async Task<bool> DeleteTemplateAsync(int id, CancellationToken cancellationToken = default)
    {
        var template = await _context.Set<DocumentTemplateEntity>()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (template == null)
        {
            return false;
        }

        _context.Set<DocumentTemplateEntity>().Remove(template);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> SetDefaultTemplateAsync(int id, CancellationToken cancellationToken = default)
    {
        var template = await _context.Set<DocumentTemplateEntity>()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (template == null)
        {
            return false;
        }

        await UnsetDefaultTemplatesAsync(template.DocumentType, id, cancellationToken);

        template.IsDefault = true;
        template.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public Task<TemplatePlaceholdersDto> GetPlaceholdersAsync(string documentType, CancellationToken cancellationToken = default)
    {
        var placeholders = new TemplatePlaceholdersDto
        {
            DocumentType = documentType,
            Placeholders = GetPlaceholdersByDocumentType(documentType)
        };

        return Task.FromResult(placeholders);
    }

    #endregion

    #region Document Generation

    public async Task<GenerateDocumentResponseDto> GenerateDocumentAsync(GenerateDocumentRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Get template
            var template = request.TemplateId.HasValue
                ? await GetTemplateByIdAsync(request.TemplateId.Value, cancellationToken)
                : await GetDefaultTemplateAsync(request.DocumentType, cancellationToken);

            if (template == null)
            {
                return new GenerateDocumentResponseDto
                {
                    Success = false,
                    Message = "Template not found"
                };
            }

            // Get document data
            var documentData = await GetDocumentDataAsync(request.DocumentType, request.EntityId, cancellationToken);
            if (documentData == null)
            {
                return new GenerateDocumentResponseDto
                {
                    Success = false,
                    Message = "Document data not found"
                };
            }

            // Generate HTML
            var html = RenderTemplate(template, documentData);

            // Convert to PDF (you would integrate a PDF library here like iTextSharp or DinkToPdf)
            var pdfBytes = await ConvertHtmlToPdfAsync(html);
            var fileName = $"{request.DocumentType}_{request.EntityId}_{DateTime.UtcNow:yyyyMMddHHmmss}.pdf";
            var filePath = Path.Combine(_uploadPath, "documents", fileName);

            // Ensure directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            // Save PDF
            await File.WriteAllBytesAsync(filePath, pdfBytes, cancellationToken);

            // Save to history
            if (request.SaveToHistory)
            {
                await SaveDocumentHistoryAsync(request.DocumentType, request.EntityId, template.Id, filePath, "Generated", userId, cancellationToken);
            }

            return new GenerateDocumentResponseDto
            {
                Success = true,
                Message = "Document generated successfully",
                FilePath = filePath,
                DownloadUrl = $"/api/document/download/{Path.GetFileName(filePath)}",
                FileContent = pdfBytes,
                FileName = fileName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating document");
            return new GenerateDocumentResponseDto
            {
                Success = false,
                Message = $"Error generating document: {ex.Message}"
            };
        }
    }

    public async Task<GenerateDocumentResponseDto> EmailDocumentAsync(EmailDocumentRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Generate the document
            var generateRequest = new GenerateDocumentRequest
            {
                DocumentType = request.DocumentType,
                EntityId = request.EntityId,
                TemplateId = request.TemplateId,
                SaveToHistory = true
            };

            var generateResult = await GenerateDocumentAsync(generateRequest, userId, cancellationToken);
            if (!generateResult.Success)
            {
                return generateResult;
            }

            // Get email template if specified
            string emailSubject = request.Subject ?? $"{request.DocumentType} - {request.EntityId}";
            string emailBody = request.MessageBody ?? $"Please find attached your {request.DocumentType}.";

            if (request.EmailTemplateId.HasValue)
            {
                var emailTemplate = await _context.Set<EmailTemplateEntity>()
                    .FirstOrDefaultAsync(t => t.Id == request.EmailTemplateId.Value, cancellationToken);

                if (emailTemplate != null)
                {
                    var documentData = await GetDocumentDataAsync(request.DocumentType, request.EntityId, cancellationToken);
                    emailSubject = RenderEmailTemplate(emailTemplate.Subject, documentData);
                    emailBody = RenderEmailTemplate(emailTemplate.BodyContent, documentData);
                }
            }

            // Get attachments if requested
            var attachments = new List<(byte[] content, string fileName, string mimeType)>();
            attachments.Add((generateResult.FileContent!, generateResult.FileName!, "application/pdf"));

            if (request.IncludeAttachments)
            {
                var entityAttachments = await GetAttachmentsAsync(request.DocumentType, request.EntityId, cancellationToken);
                foreach (var attachment in entityAttachments.Attachments.Where(a => a.IncludeInEmail))
                {
                    var (stream, fileName, mimeType) = await DownloadAttachmentAsync(attachment.Id, cancellationToken);
                    if (stream != null)
                    {
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms, cancellationToken);
                        attachments.Add((ms.ToArray(), fileName!, mimeType!));
                    }
                }
            }

            // Send email
            var emailSent = await _emailService.SendEmailAsync(
                request.RecipientEmail,
                emailSubject,
                emailBody,
                attachments,
                request.CcEmails?.ToArray());

            // Save to history
            await SaveDocumentHistoryAsync(
                request.DocumentType,
                request.EntityId,
                request.TemplateId,
                generateResult.FilePath,
                "Emailed",
                userId,
                cancellationToken,
                request.RecipientEmail,
                emailSubject,
                emailSent,
                emailSent ? null : "Failed to send email");

            return new GenerateDocumentResponseDto
            {
                Success = emailSent,
                Message = emailSent ? "Document emailed successfully" : "Failed to send email",
                FilePath = generateResult.FilePath,
                FileName = generateResult.FileName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error emailing document");
            return new GenerateDocumentResponseDto
            {
                Success = false,
                Message = $"Error emailing document: {ex.Message}"
            };
        }
    }

    #endregion

    #region Document Attachments

    public async Task<DocumentAttachmentDto> UploadAttachmentAsync(UploadAttachmentRequest request, Stream fileStream, string fileName, string mimeType, Guid userId, CancellationToken cancellationToken = default)
    {
        // Generate unique filename
        var fileExtension = Path.GetExtension(fileName);
        var storedFileName = $"{Guid.NewGuid()}{fileExtension}";
        var attachmentPath = Path.Combine(_uploadPath, "attachments", request.EntityType, request.EntityId.ToString());

        // Ensure directory exists
        Directory.CreateDirectory(attachmentPath);

        var fullPath = Path.Combine(attachmentPath, storedFileName);

        // Save file
        using (var fileStreamDisk = File.Create(fullPath))
        {
            await fileStream.CopyToAsync(fileStreamDisk, cancellationToken);
        }

        // Get file size
        var fileInfo = new FileInfo(fullPath);

        // Create attachment record
        var attachment = new DocumentAttachmentEntity
        {
            EntityType = request.EntityType,
            EntityId = request.EntityId,
            FileName = fileName,
            StoredFileName = fullPath,
            MimeType = mimeType,
            FileSizeBytes = fileInfo.Length,
            Description = request.Description,
            IncludeInEmail = request.IncludeInEmail,
            UploadedByUserId = userId,
            UploadedAt = DateTime.UtcNow
        };

        _context.Set<DocumentAttachmentEntity>().Add(attachment);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(attachment);
    }

    public async Task<DocumentAttachmentListResponseDto> GetAttachmentsAsync(string entityType, int entityId, CancellationToken cancellationToken = default)
    {
        var attachments = await _context.Set<DocumentAttachmentEntity>()
            .Include(a => a.UploadedByUser)
            .Where(a => a.EntityType == entityType && a.EntityId == entityId)
            .OrderByDescending(a => a.UploadedAt)
            .ToListAsync(cancellationToken);

        return new DocumentAttachmentListResponseDto
        {
            Attachments = attachments.Select(MapToDto).ToList(),
            TotalCount = attachments.Count
        };
    }

    public async Task<(Stream? stream, string? fileName, string? mimeType)> DownloadAttachmentAsync(int id, CancellationToken cancellationToken = default)
    {
        var attachment = await _context.Set<DocumentAttachmentEntity>()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (attachment == null || !File.Exists(attachment.StoredFileName))
        {
            return (null, null, null);
        }

        var stream = File.OpenRead(attachment.StoredFileName);
        return (stream, attachment.FileName, attachment.MimeType);
    }

    public async Task<bool> DeleteAttachmentAsync(int id, CancellationToken cancellationToken = default)
    {
        var attachment = await _context.Set<DocumentAttachmentEntity>()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (attachment == null)
        {
            return false;
        }

        // Delete file
        if (File.Exists(attachment.StoredFileName))
        {
            File.Delete(attachment.StoredFileName);
        }

        _context.Set<DocumentAttachmentEntity>().Remove(attachment);
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    #endregion

    #region Document History

    public async Task<DocumentHistoryListResponseDto> GetDocumentHistoryAsync(string? documentType = null, int? entityId = null, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var query = _context.Set<DocumentHistoryEntity>()
            .Include(h => h.Template)
            .Include(h => h.GeneratedByUser)
            .AsQueryable();

        if (!string.IsNullOrEmpty(documentType))
        {
            query = query.Where(h => h.DocumentType == documentType);
        }

        if (entityId.HasValue)
        {
            query = query.Where(h => h.EntityId == entityId.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var history = await query
            .OrderByDescending(h => h.GeneratedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new DocumentHistoryListResponseDto
        {
            History = history.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    #endregion

    #region Digital Signatures

    public async Task<DocumentSignatureDto> CreateSignatureAsync(CreateSignatureRequest request, Guid? userId, string? ipAddress, string? deviceInfo, CancellationToken cancellationToken = default)
    {
        var signature = new DocumentSignatureEntity
        {
            DocumentType = request.DocumentType,
            DocumentId = request.DocumentId,
            SignerName = request.SignerName,
            SignerEmail = request.SignerEmail,
            SignerRole = request.SignerRole,
            SignatureData = request.SignatureData,
            IpAddress = ipAddress,
            DeviceInfo = deviceInfo,
            UserId = userId,
            SignedAt = DateTime.UtcNow
        };

        _context.Set<DocumentSignatureEntity>().Add(signature);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(signature);
    }

    public async Task<DocumentSignatureListResponseDto> GetSignaturesAsync(string documentType, int documentId, CancellationToken cancellationToken = default)
    {
        var signatures = await _context.Set<DocumentSignatureEntity>()
            .Where(s => s.DocumentType == documentType && s.DocumentId == documentId)
            .OrderBy(s => s.SignedAt)
            .ToListAsync(cancellationToken);

        return new DocumentSignatureListResponseDto
        {
            Signatures = signatures.Select(MapToDto).ToList(),
            TotalCount = signatures.Count
        };
    }

    public async Task<bool> VerifySignatureAsync(int signatureId, CancellationToken cancellationToken = default)
    {
        var signature = await _context.Set<DocumentSignatureEntity>()
            .FirstOrDefaultAsync(s => s.Id == signatureId, cancellationToken);

        if (signature == null)
        {
            return false;
        }

        signature.IsVerified = true;
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }

    #endregion

    #region Email Templates

    public async Task<EmailTemplateDto?> GetEmailTemplateByCodeAsync(string templateCode, CancellationToken cancellationToken = default)
    {
        var template = await _context.Set<EmailTemplateEntity>()
            .FirstOrDefaultAsync(t => t.TemplateCode == templateCode && t.IsActive, cancellationToken);

        return template == null ? null : MapToDto(template);
    }

    public async Task<EmailTemplateListResponseDto> GetAllEmailTemplatesAsync(bool? activeOnly = null, CancellationToken cancellationToken = default)
    {
        var query = _context.Set<EmailTemplateEntity>().AsQueryable();

        if (activeOnly == true)
        {
            query = query.Where(t => t.IsActive);
        }

        var templates = await query
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        return new EmailTemplateListResponseDto
        {
            Templates = templates.Select(MapToDto).ToList(),
            TotalCount = templates.Count
        };
    }

    public async Task<EmailTemplateDto> CreateEmailTemplateAsync(UpsertEmailTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var template = new EmailTemplateEntity
        {
            Name = request.Name,
            TemplateCode = request.TemplateCode,
            Subject = request.Subject,
            BodyContent = request.BodyContent,
            CcEmails = request.CcEmails,
            BccEmails = request.BccEmails,
            IsActive = request.IsActive,
            CreatedAt = DateTime.UtcNow
        };

        _context.Set<EmailTemplateEntity>().Add(template);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(template);
    }

    public async Task<EmailTemplateDto> UpdateEmailTemplateAsync(int id, UpsertEmailTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var template = await _context.Set<EmailTemplateEntity>()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (template == null)
        {
            throw new Exception($"Email template with ID {id} not found");
        }

        template.Name = request.Name;
        template.TemplateCode = request.TemplateCode;
        template.Subject = request.Subject;
        template.BodyContent = request.BodyContent;
        template.CcEmails = request.CcEmails;
        template.BccEmails = request.BccEmails;
        template.IsActive = request.IsActive;
        template.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(template);
    }

    #endregion

    #region Private Helper Methods

    private async Task UnsetDefaultTemplatesAsync(string documentType, int? excludeId = null, CancellationToken cancellationToken = default)
    {
        var templates = await _context.Set<DocumentTemplateEntity>()
            .Where(t => t.DocumentType == documentType && t.IsDefault)
            .ToListAsync(cancellationToken);

        foreach (var template in templates)
        {
            if (excludeId == null || template.Id != excludeId)
            {
                template.IsDefault = false;
            }
        }
    }

    private string RenderTemplate(DocumentTemplateDto template, Dictionary<string, string> data)
    {
        var html = new StringBuilder();

        // Add CSS
        if (!string.IsNullOrEmpty(template.CssStyles))
        {
            html.Append($"<style>{template.CssStyles}</style>");
        }

        // Add header
        if (!string.IsNullOrEmpty(template.HeaderContent))
        {
            html.Append(ReplacePlaceholders(template.HeaderContent, data));
        }

        // Add main content
        html.Append(ReplacePlaceholders(template.HtmlContent, data));

        // Add footer
        if (!string.IsNullOrEmpty(template.FooterContent))
        {
            html.Append(ReplacePlaceholders(template.FooterContent, data));
        }

        return html.ToString();
    }

    private string RenderEmailTemplate(string template, Dictionary<string, string> data)
    {
        return ReplacePlaceholders(template, data);
    }

    private string ReplacePlaceholders(string content, Dictionary<string, string> data)
    {
        foreach (var kvp in data)
        {
            content = content.Replace($"{{{{{kvp.Key}}}}}", kvp.Value);
        }
        return content;
    }

    private async Task<Dictionary<string, string>> GetDocumentDataAsync(string documentType, int entityId, CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string>
        {
            { "Date", DateTime.Now.ToString("yyyy-MM-dd") },
            { "Time", DateTime.Now.ToString("HH:mm:ss") },
            { "Year", DateTime.Now.Year.ToString() }
        };

        // Fetch data based on document type
        switch (documentType)
        {
            case "Invoice":
                var invoice = await _context.Invoices
                    .Include(i => i.DocumentLines)
                    .FirstOrDefaultAsync(i => i.Id == entityId, cancellationToken);
                if (invoice != null)
                {
                    data["InvoiceNumber"] = invoice.SAPDocNum?.ToString() ?? invoice.Id.ToString();
                    data["CustomerCode"] = invoice.CardCode;
                    data["CustomerName"] = invoice.CardName ?? "";
                    data["DocDate"] = invoice.DocDate.ToString("yyyy-MM-dd");
                    data["DueDate"] = invoice.DocDueDate?.ToString("yyyy-MM-dd") ?? "";
                    data["Total"] = invoice.DocTotal.ToString("N2");
                    data["Currency"] = invoice.DocCurrency ?? "USD";
                    data["VatSum"] = invoice.VatSum.ToString("N2");
                }
                break;
                // Add other document types as needed
        }

        return data;
    }

    private Task<byte[]> ConvertHtmlToPdfAsync(string html)
    {
        // This is a placeholder. In production, use a PDF library like:
        // - DinkToPdf
        // - iTextSharp
        // - PuppeteerSharp
        // For now, return empty bytes
        _logger.LogWarning("PDF conversion not implemented. Using placeholder.");
        return Task.FromResult(Encoding.UTF8.GetBytes(html));
    }

    private async Task SaveDocumentHistoryAsync(
        string documentType,
        int entityId,
        int? templateId,
        string? filePath,
        string action,
        Guid userId,
        CancellationToken cancellationToken,
        string? recipientEmail = null,
        string? emailSubject = null,
        bool? emailSent = null,
        string? emailError = null)
    {
        var history = new DocumentHistoryEntity
        {
            DocumentType = documentType,
            EntityId = entityId,
            TemplateId = templateId,
            FilePath = filePath,
            Action = action,
            RecipientEmail = recipientEmail,
            EmailSubject = emailSubject,
            EmailSent = emailSent,
            EmailError = emailError,
            GeneratedByUserId = userId,
            GeneratedAt = DateTime.UtcNow
        };

        _context.Set<DocumentHistoryEntity>().Add(history);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private List<PlaceholderDto> GetPlaceholdersByDocumentType(string documentType)
    {
        var common = new List<PlaceholderDto>
        {
            new() { Key = "Date", Description = "Current date", Example = "2026-01-12", Category = "System" },
            new() { Key = "Time", Description = "Current time", Example = "14:30:00", Category = "System" },
            new() { Key = "Year", Description = "Current year", Example = "2026", Category = "System" }
        };

        var specific = documentType switch
        {
            "Invoice" => new List<PlaceholderDto>
            {
                new() { Key = "InvoiceNumber", Description = "Invoice number", Example = "INV-001", Category = "Invoice" },
                new() { Key = "CustomerCode", Description = "Customer code", Example = "C001", Category = "Customer" },
                new() { Key = "CustomerName", Description = "Customer name", Example = "John Doe", Category = "Customer" },
                new() { Key = "DocDate", Description = "Document date", Example = "2026-01-12", Category = "Invoice" },
                new() { Key = "DueDate", Description = "Due date", Example = "2026-02-12", Category = "Invoice" },
                new() { Key = "Total", Description = "Total amount", Example = "1,500.00", Category = "Invoice" },
                new() { Key = "Currency", Description = "Currency code", Example = "USD", Category = "Invoice" },
                new() { Key = "VatSum", Description = "VAT amount", Example = "195.00", Category = "Invoice" }
            },
            _ => new List<PlaceholderDto>()
        };

        return common.Concat(specific).ToList();
    }

    private DocumentTemplateDto MapToDto(DocumentTemplateEntity entity)
    {
        return new DocumentTemplateDto
        {
            Id = entity.Id,
            Name = entity.Name,
            DocumentType = entity.DocumentType,
            HtmlContent = entity.HtmlContent,
            CssStyles = entity.CssStyles,
            HeaderContent = entity.HeaderContent,
            FooterContent = entity.FooterContent,
            PaperSize = entity.PaperSize,
            Orientation = entity.Orientation,
            IsDefault = entity.IsDefault,
            IsActive = entity.IsActive,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            CreatedByUserName = entity.CreatedByUser?.Username
        };
    }

    private DocumentAttachmentDto MapToDto(DocumentAttachmentEntity entity)
    {
        return new DocumentAttachmentDto
        {
            Id = entity.Id,
            EntityType = entity.EntityType,
            EntityId = entity.EntityId,
            FileName = entity.FileName,
            MimeType = entity.MimeType,
            FileSizeBytes = entity.FileSizeBytes,
            FileSizeFormatted = FormatFileSize(entity.FileSizeBytes),
            Description = entity.Description,
            IncludeInEmail = entity.IncludeInEmail,
            UploadedAt = entity.UploadedAt,
            UploadedByUserName = entity.UploadedByUser?.Username,
            DownloadUrl = $"/api/document/attachment/{entity.Id}/download"
        };
    }

    private DocumentHistoryDto MapToDto(DocumentHistoryEntity entity)
    {
        return new DocumentHistoryDto
        {
            Id = entity.Id,
            DocumentType = entity.DocumentType,
            EntityId = entity.EntityId,
            DocumentNumber = entity.DocumentNumber,
            TemplateName = entity.Template?.Name,
            Action = entity.Action,
            RecipientEmail = entity.RecipientEmail,
            EmailSubject = entity.EmailSubject,
            EmailSent = entity.EmailSent,
            EmailError = entity.EmailError,
            GeneratedAt = entity.GeneratedAt,
            GeneratedByUserName = entity.GeneratedByUser?.Username,
            DownloadUrl = !string.IsNullOrEmpty(entity.FilePath) ? $"/api/document/history/{entity.Id}/download" : null
        };
    }

    private DocumentSignatureDto MapToDto(DocumentSignatureEntity entity)
    {
        return new DocumentSignatureDto
        {
            Id = entity.Id,
            DocumentType = entity.DocumentType,
            DocumentId = entity.DocumentId,
            SignerName = entity.SignerName,
            SignerEmail = entity.SignerEmail,
            SignerRole = entity.SignerRole,
            SignatureData = entity.SignatureData,
            SignedAt = entity.SignedAt,
            IsVerified = entity.IsVerified,
            IpAddress = entity.IpAddress
        };
    }

    private EmailTemplateDto MapToDto(EmailTemplateEntity entity)
    {
        return new EmailTemplateDto
        {
            Id = entity.Id,
            Name = entity.Name,
            TemplateCode = entity.TemplateCode,
            Subject = entity.Subject,
            BodyContent = entity.BodyContent,
            CcEmails = entity.CcEmails,
            BccEmails = entity.BccEmails,
            IsActive = entity.IsActive,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    #endregion
}
