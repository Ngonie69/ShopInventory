using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Pods;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using System.Text;
using System.Text.RegularExpressions;

namespace ShopInventory.Services;

public class PodStatusInfo
{
    public DateTime UploadedAt { get; set; }
    public string? UploadedBy { get; set; }
    public int Count { get; set; }
    public List<PodUploadUserSummaryInfo> UploadedByUsers { get; set; } = [];
}

public class PodUploadUserSummaryInfo
{
    public string Username { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public DateTime? LatestUploadedAt { get; set; }
}

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
    Task<DocumentAttachmentDto> UploadAttachmentAsync(UploadAttachmentRequest request, Stream fileStream, string fileName, string mimeType, Guid? userId, CancellationToken cancellationToken = default);
    Task<DocumentAttachmentListResponseDto> GetAttachmentsAsync(string entityType, int entityId, CancellationToken cancellationToken = default);
    Task<(Stream? stream, string? fileName, string? mimeType)> DownloadAttachmentAsync(int id, CancellationToken cancellationToken = default);
    Task<bool> DeleteAttachmentAsync(int id, CancellationToken cancellationToken = default);

    // POD (Proof of Delivery)
    Task<PodAttachmentListResponseDto> GetAllPodAttachmentsAsync(int page = 1, int pageSize = 20, string? cardCode = null, CancellationToken cancellationToken = default, DateTime? fromDate = null, DateTime? toDate = null, string? search = null, Guid? uploadedByUserId = null, string? assignedSection = null);
    Task<List<int>> GetScopedPodInvoiceDocEntriesAsync(IEnumerable<int> candidateDocEntries, string assignedSection, CancellationToken cancellationToken = default);
    Task EnsureInvoiceCachedAsync(int sapDocEntry, int sapDocNum, string cardCode, string? cardName, CancellationToken cancellationToken = default);
    Task<Dictionary<int, PodStatusInfo>> GetPodStatusByDocEntriesAsync(List<int> docEntries, CancellationToken cancellationToken = default);

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
    private readonly ISAPServiceLayerClient _sapServiceLayerClient;
    private readonly ILogger<DocumentService> _logger;
    private readonly string _uploadPath;

    public DocumentService(
        ApplicationDbContext context,
        IEmailService emailService,
        ISAPServiceLayerClient sapServiceLayerClient,
        ILogger<DocumentService> logger,
        IConfiguration configuration)
    {
        _context = context;
        _emailService = emailService;
        _sapServiceLayerClient = sapServiceLayerClient;
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
            .AsNoTracking()
            .Include(t => t.CreatedByUser)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        return template == null ? null : MapToDto(template);
    }

    public async Task<DocumentTemplateDto?> GetDefaultTemplateAsync(string documentType, CancellationToken cancellationToken = default)
    {
        var template = await _context.Set<DocumentTemplateEntity>()
            .AsNoTracking()
            .Include(t => t.CreatedByUser)
            .FirstOrDefaultAsync(t => t.DocumentType == documentType && t.IsDefault && t.IsActive, cancellationToken);

        return template == null ? null : MapToDto(template);
    }

    public async Task<DocumentTemplateListResponseDto> GetAllTemplatesAsync(string? documentType = null, bool? activeOnly = null, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var query = _context.Set<DocumentTemplateEntity>()
            .AsNoTracking()
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

    /// <summary>
    /// Image MIME types eligible for compression.
    /// </summary>
    private static readonly HashSet<string> CompressibleImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };

    /// <summary>Max dimension (width or height) for uploaded images.</summary>
    private const int MaxImageDimension = 1920;

    /// <summary>JPEG quality for compressed images (1-100).</summary>
    private const int JpegCompressionQuality = 75;

    public async Task<DocumentAttachmentDto> UploadAttachmentAsync(UploadAttachmentRequest request, Stream fileStream, string fileName, string mimeType, Guid? userId, CancellationToken cancellationToken = default)
    {
        var isCompressibleImage = CompressibleImageTypes.Contains(mimeType);

        // Compressed images are always saved as JPEG
        var fileExtension = isCompressibleImage ? ".jpg" : Path.GetExtension(fileName);
        var storedFileName = $"{Guid.NewGuid()}{fileExtension}";
        var attachmentPath = Path.Combine(_uploadPath, "attachments", request.EntityType, request.EntityId.ToString());

        // Ensure directory exists
        Directory.CreateDirectory(attachmentPath);

        var fullPath = Path.Combine(attachmentPath, storedFileName);

        if (isCompressibleImage)
        {
            // Compress and resize the image before saving
            await CompressAndSaveImageAsync(fileStream, fullPath, cancellationToken);
            mimeType = "image/jpeg";
        }
        else
        {
            // Save non-image files as-is
            using var fileStreamDisk = File.Create(fullPath);
            await fileStream.CopyToAsync(fileStreamDisk, cancellationToken);
        }

        // Get file size
        var fileInfo = new FileInfo(fullPath);

        // Create attachment record
        var attachment = new DocumentAttachmentEntity
        {
            EntityType = request.EntityType,
            EntityId = request.EntityId,
            ExternalReference = request.ExternalReference,
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

    private async Task CompressAndSaveImageAsync(Stream sourceStream, string outputPath, CancellationToken cancellationToken)
    {
        using var image = await Image.LoadAsync(sourceStream, cancellationToken);

        // Resize if larger than max dimension while preserving aspect ratio
        if (image.Width > MaxImageDimension || image.Height > MaxImageDimension)
        {
            var options = new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(MaxImageDimension, MaxImageDimension)
            };
            image.Mutate(x => x.Resize(options));
        }

        // Auto-orient based on EXIF data (phone photos are often rotated)
        image.Mutate(x => x.AutoOrient());

        var encoder = new JpegEncoder { Quality = JpegCompressionQuality };
        await image.SaveAsync(outputPath, encoder, cancellationToken);
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

    public async Task<PodAttachmentListResponseDto> GetAllPodAttachmentsAsync(int page = 1, int pageSize = 20, string? cardCode = null, CancellationToken cancellationToken = default, DateTime? fromDate = null, DateTime? toDate = null, string? search = null, Guid? uploadedByUserId = null, string? assignedSection = null)
    {
        var excludedCardCodes = PodExclusions.ExcludedCardCodes.ToArray();

        // Get DocEntries for excluded BPs so we can filter them out
        var excludedDocEntries = _context.Invoices
            .Where(i => i.SAPDocEntry != null && i.CardCode != null && excludedCardCodes.Contains(i.CardCode.ToUpper()))
            .Select(i => i.SAPDocEntry!.Value);

        var query = _context.Set<DocumentAttachmentEntity>()
            .Include(a => a.UploadedByUser)
            .Where(a => a.EntityType == "Invoice")
            .Where(a => !excludedDocEntries.Contains(a.EntityId))
            .Where(a =>
                EF.Functions.ILike(a.FileName, "%pod%") ||
                EF.Functions.ILike(a.FileName, "%proof of delivery%") ||
                (a.Description != null && (EF.Functions.ILike(a.Description, "%pod%") || EF.Functions.ILike(a.Description, "%proof of delivery%")))
            )
            .AsQueryable();

        // Filter by uploading user (used for Driver role - they only see their own PODs)
        if (uploadedByUserId.HasValue)
        {
            query = query.Where(a => a.UploadedByUserId == uploadedByUserId.Value);
        }

        if (fromDate.HasValue)
        {
            // UploadedAt is timestamptz — Npgsql requires DateTime.Utc for comparisons
            var fromUtc = DateTime.SpecifyKind(fromDate.Value.Date, DateTimeKind.Utc);
            query = query.Where(a => a.UploadedAt >= fromUtc);
        }

        if (toDate.HasValue)
        {
            var toUtc = DateTime.SpecifyKind(toDate.Value.Date.AddDays(1), DateTimeKind.Utc);
            query = query.Where(a => a.UploadedAt < toUtc);
        }

        if (!string.IsNullOrEmpty(cardCode))
        {
            // Support comma-separated card codes for multi-account customers
            var cardCodes = cardCode.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var invoiceDocEntries = _context.Invoices
                .Where(i => cardCodes.Contains(i.CardCode) && i.SAPDocEntry != null)
                .Select(i => i.SAPDocEntry!.Value);

            query = query.Where(a => invoiceDocEntries.Contains(a.EntityId));
        }

        // Free-text search across invoice number, customer name, and customer code
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var pattern = $"%{term}%";
            var matchingDocEntries = _context.Invoices
                .Where(i => i.SAPDocEntry != null && (
                    (i.SAPDocNum != null && i.SAPDocNum.ToString()!.Contains(term)) ||
                    (i.CardName != null && EF.Functions.ILike(i.CardName, pattern)) ||
                    EF.Functions.ILike(i.CardCode, pattern)
                ))
                .Select(i => i.SAPDocEntry!.Value);

            query = query.Where(a => matchingDocEntries.Contains(a.EntityId));
        }

        if (!string.IsNullOrWhiteSpace(assignedSection))
        {
            var candidateDocEntries = await query
                .Select(a => a.EntityId)
                .Distinct()
                .ToListAsync(cancellationToken);

            var scopedDocEntries = await GetScopedPodInvoiceDocEntriesAsync(candidateDocEntries, assignedSection, cancellationToken);
            if (scopedDocEntries.Count == 0)
            {
                return new PodAttachmentListResponseDto
                {
                    Items = [],
                    TotalCount = 0,
                    Page = page,
                    PageSize = pageSize,
                    HasMore = false
                };
            }

            query = query.Where(a => scopedDocEntries.Contains(a.EntityId));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var attachments = await query
            .OrderByDescending(a => a.UploadedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var entityIds = attachments.Select(a => a.EntityId).Distinct().ToList();
        var invoices = await _context.Invoices
            .Where(i => i.SAPDocEntry != null && entityIds.Contains(i.SAPDocEntry.Value))
            .Select(i => new
            {
                DocEntry = i.SAPDocEntry!.Value,
                DocNum = i.SAPDocNum ?? 0,
                i.CardCode,
                i.CardName,
                i.CreatedAt,
                i.UpdatedAt,
                i.Id
            })
            .ToListAsync(cancellationToken);

        var duplicateDocEntries = invoices
            .GroupBy(i => i.DocEntry)
            .Count(g => g.Count() > 1);

        if (duplicateDocEntries > 0)
        {
            _logger.LogWarning(
                "Detected {DuplicateCount} duplicate cached invoice doc entries while loading POD attachments",
                duplicateDocEntries);
        }

        var invoiceLookup = invoices
            .GroupBy(i => i.DocEntry)
            .ToDictionary(
                g => g.Key,
                g => g
                    .OrderByDescending(i => i.DocNum > 0)
                    .ThenByDescending(i => !string.IsNullOrWhiteSpace(i.CardCode))
                    .ThenByDescending(i => !string.IsNullOrWhiteSpace(i.CardName))
                    .ThenByDescending(i => i.UpdatedAt ?? i.CreatedAt)
                    .ThenByDescending(i => i.Id)
                    .First());

        var items = attachments.Select(a =>
        {
            var dto = MapToDto(a);
            invoiceLookup.TryGetValue(a.EntityId, out var inv);
            return new PodAttachmentItemDto
            {
                Id = dto.Id,
                FileName = dto.FileName,
                MimeType = dto.MimeType,
                FileSizeBytes = dto.FileSizeBytes,
                FileSizeFormatted = dto.FileSizeFormatted,
                Description = dto.Description,
                UploadedAt = dto.UploadedAt,
                UploadedByUserName = dto.UploadedByUserName,
                DownloadUrl = dto.DownloadUrl,
                InvoiceDocEntry = a.EntityId,
                InvoiceDocNum = inv?.DocNum ?? 0,
                CardCode = inv?.CardCode,
                CardName = inv?.CardName
            };
        }).ToList();

        return new PodAttachmentListResponseDto
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            HasMore = (page * pageSize) < totalCount
        };
    }

    public async Task<List<int>> GetScopedPodInvoiceDocEntriesAsync(
        IEnumerable<int> candidateDocEntries,
        string assignedSection,
        CancellationToken cancellationToken = default)
    {
        var distinctDocEntries = candidateDocEntries
            .Where(docEntry => docEntry > 0)
            .Distinct()
            .ToList();

        if (distinctDocEntries.Count == 0 || string.IsNullOrWhiteSpace(assignedSection))
        {
            return [];
        }

        var invoices = await _sapServiceLayerClient.GetInvoiceHeadersByDocEntriesAsync(distinctDocEntries, cancellationToken);
        if (invoices.Count == 0)
        {
            return [];
        }

        var warehouseLocations = PodLocationScope.BuildWarehouseLocationLookup(
            await _sapServiceLayerClient.GetWarehousesAsync(cancellationToken));

        return invoices
            .Where(invoice => PodLocationScope.InvoiceMatchesAssignedSection(invoice, assignedSection.Trim(), warehouseLocations))
            .Select(invoice => invoice.DocEntry)
            .Distinct()
            .ToList();
    }

    public async Task EnsureInvoiceCachedAsync(int sapDocEntry, int sapDocNum, string cardCode, string? cardName, CancellationToken cancellationToken = default)
    {
        var existing = await _context.Invoices
            .FirstOrDefaultAsync(i => i.SAPDocEntry == sapDocEntry, cancellationToken);

        if (existing != null)
        {
            // Update if DocNum or customer info was missing
            if (existing.SAPDocNum == null || existing.SAPDocNum == 0)
                existing.SAPDocNum = sapDocNum;
            if (string.IsNullOrEmpty(existing.CardCode))
                existing.CardCode = cardCode;
            if (string.IsNullOrEmpty(existing.CardName))
                existing.CardName = cardName;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _context.Invoices.Add(new Models.Entities.InvoiceEntity
            {
                SAPDocEntry = sapDocEntry,
                SAPDocNum = sapDocNum,
                CardCode = cardCode,
                CardName = cardName,
                DocDate = DateTime.UtcNow,
                Status = "Synced",
                SyncedToSAP = true,
                SyncedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
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
            ExternalReference = entity.ExternalReference,
            FileName = entity.FileName,
            MimeType = entity.MimeType,
            FileSizeBytes = entity.FileSizeBytes,
            FileSizeFormatted = FormatFileSize(entity.FileSizeBytes),
            Description = entity.Description,
            IncludeInEmail = entity.IncludeInEmail,
            UploadedAt = entity.UploadedAt,
            UploadedByUserName = entity.UploadedByUser?.Username,
            DownloadUrl = $"/api/document/attachments/{entity.Id}/download"
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

    public async Task<Dictionary<int, PodStatusInfo>> GetPodStatusByDocEntriesAsync(List<int> docEntries, CancellationToken cancellationToken = default)
    {
        if (docEntries.Count == 0)
            return new Dictionary<int, PodStatusInfo>();

        var podAttachments = await _context.Set<DocumentAttachmentEntity>()
            .AsNoTracking()
            .Include(a => a.UploadedByUser)
            .Where(a => a.EntityType == "Invoice" && docEntries.Contains(a.EntityId))
            .Where(a =>
                EF.Functions.ILike(a.FileName, "%pod%") ||
                EF.Functions.ILike(a.FileName, "%proof of delivery%") ||
                (a.Description != null && (EF.Functions.ILike(a.Description, "%pod%") || EF.Functions.ILike(a.Description, "%proof of delivery%")))
            )
            .Select(a => new
            {
                DocEntry = a.EntityId,
                a.UploadedAt,
                UploadedBy = a.UploadedByUser != null ? a.UploadedByUser.Username : null
            })
            .ToListAsync(cancellationToken);

        var podData = podAttachments
            .GroupBy(a => a.DocEntry)
            .Select(group =>
            {
                var latestAttachment = group
                    .OrderByDescending(a => a.UploadedAt)
                    .First();

                var uploadedByUsers = group
                    .GroupBy(
                        a => string.IsNullOrWhiteSpace(a.UploadedBy) ? "Unknown uploader" : a.UploadedBy!.Trim(),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(uploaderGroup => new PodUploadUserSummaryInfo
                    {
                        Username = uploaderGroup.Key,
                        FileCount = uploaderGroup.Count(),
                        LatestUploadedAt = uploaderGroup.Max(a => a.UploadedAt)
                    })
                    .OrderByDescending(uploader => uploader.LatestUploadedAt)
                    .ThenBy(uploader => uploader.Username)
                    .ToList();

                return new
                {
                    DocEntry = group.Key,
                    LatestUploadedAt = latestAttachment.UploadedAt,
                    UploadedBy = string.IsNullOrWhiteSpace(latestAttachment.UploadedBy)
                        ? null
                        : latestAttachment.UploadedBy.Trim(),
                    Count = group.Count(),
                    UploadedByUsers = uploadedByUsers
                };
            })
            .ToList();

        return podData.ToDictionary(
            p => p.DocEntry,
            p => new PodStatusInfo
            {
                UploadedAt = p.LatestUploadedAt,
                UploadedBy = p.UploadedBy,
                Count = p.Count,
                UploadedByUsers = p.UploadedByUsers
            });
    }

    #endregion
}
