using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ShopInventory.Models.Entities;

/// <summary>
/// Document template entity for customizable invoice, quote, and other document templates
/// </summary>
[Table("DocumentTemplates")]
public class DocumentTemplateEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Template name
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Document type: Invoice, Quote, SalesOrder, PurchaseOrder, DeliveryNote, CreditNote
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>
    /// Template content in HTML format with placeholders
    /// </summary>
    [Required]
    public string HtmlContent { get; set; } = string.Empty;

    /// <summary>
    /// CSS styles for the template
    /// </summary>
    public string? CssStyles { get; set; }

    /// <summary>
    /// Header content (company logo, address, etc.)
    /// </summary>
    public string? HeaderContent { get; set; }

    /// <summary>
    /// Footer content (terms, conditions, signatures, etc.)
    /// </summary>
    public string? FooterContent { get; set; }

    /// <summary>
    /// Paper size: A4, Letter, Legal
    /// </summary>
    [MaxLength(20)]
    public string PaperSize { get; set; } = "A4";

    /// <summary>
    /// Orientation: Portrait, Landscape
    /// </summary>
    [MaxLength(20)]
    public string Orientation { get; set; } = "Portrait";

    /// <summary>
    /// Whether this is the default template for the document type
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Whether this template is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Updated timestamp
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// User who created the template
    /// </summary>
    public Guid? CreatedByUserId { get; set; }

    // Navigation properties
    public virtual User? CreatedByUser { get; set; }
}

/// <summary>
/// Document attachment entity for storing files attached to orders, invoices, etc.
/// </summary>
[Table("DocumentAttachments")]
public class DocumentAttachmentEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Reference entity type: Invoice, SalesOrder, PurchaseOrder, Customer, Supplier
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// Reference entity ID
    /// </summary>
    public int EntityId { get; set; }

    /// <summary>
    /// Original filename
    /// </summary>
    [Required]
    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Stored filename (with path)
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string StoredFileName { get; set; } = string.Empty;

    /// <summary>
    /// File MIME type
    /// </summary>
    [MaxLength(100)]
    public string? MimeType { get; set; }

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// File description or notes
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Whether this attachment should be included when emailing documents
    /// </summary>
    public bool IncludeInEmail { get; set; } = true;

    /// <summary>
    /// Uploaded timestamp
    /// </summary>
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who uploaded the attachment
    /// </summary>
    public Guid? UploadedByUserId { get; set; }

    // Navigation properties
    public virtual User? UploadedByUser { get; set; }
}

/// <summary>
/// Document generation history
/// </summary>
[Table("DocumentHistory")]
public class DocumentHistoryEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Document type
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>
    /// Reference entity ID
    /// </summary>
    public int EntityId { get; set; }

    /// <summary>
    /// Document number (invoice number, order number, etc.)
    /// </summary>
    [MaxLength(50)]
    public string? DocumentNumber { get; set; }

    /// <summary>
    /// Template used for generation
    /// </summary>
    public int? TemplateId { get; set; }

    /// <summary>
    /// Generated file path (PDF)
    /// </summary>
    [MaxLength(500)]
    public string? FilePath { get; set; }

    /// <summary>
    /// Generation action: Generated, Emailed, Downloaded, Printed
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Recipient email (if emailed)
    /// </summary>
    [MaxLength(255)]
    public string? RecipientEmail { get; set; }

    /// <summary>
    /// Email subject (if emailed)
    /// </summary>
    [MaxLength(500)]
    public string? EmailSubject { get; set; }

    /// <summary>
    /// Whether email was sent successfully
    /// </summary>
    public bool? EmailSent { get; set; }

    /// <summary>
    /// Email error message (if failed)
    /// </summary>
    [MaxLength(1000)]
    public string? EmailError { get; set; }

    /// <summary>
    /// Generated timestamp
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who generated the document
    /// </summary>
    public Guid? GeneratedByUserId { get; set; }

    // Navigation properties
    public virtual User? GeneratedByUser { get; set; }
    public virtual DocumentTemplateEntity? Template { get; set; }
}

/// <summary>
/// Digital signature entity
/// </summary>
[Table("DocumentSignatures")]
public class DocumentSignatureEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Reference document type
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>
    /// Reference document ID
    /// </summary>
    public int DocumentId { get; set; }

    /// <summary>
    /// Signer's name
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string SignerName { get; set; } = string.Empty;

    /// <summary>
    /// Signer's email
    /// </summary>
    [MaxLength(255)]
    public string? SignerEmail { get; set; }

    /// <summary>
    /// Signer's role: Customer, Supplier, Manager, Accountant
    /// </summary>
    [MaxLength(50)]
    public string SignerRole { get; set; } = string.Empty;

    /// <summary>
    /// Signature image data (base64)
    /// </summary>
    [Required]
    public string SignatureData { get; set; } = string.Empty;

    /// <summary>
    /// IP address from which signature was captured
    /// </summary>
    [MaxLength(50)]
    public string? IpAddress { get; set; }

    /// <summary>
    /// Device information
    /// </summary>
    [MaxLength(500)]
    public string? DeviceInfo { get; set; }

    /// <summary>
    /// Signature timestamp
    /// </summary>
    public DateTime SignedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the signature is verified
    /// </summary>
    public bool IsVerified { get; set; } = true;

    /// <summary>
    /// User ID (if system user)
    /// </summary>
    public Guid? UserId { get; set; }

    // Navigation properties
    public virtual User? User { get; set; }
}

/// <summary>
/// Email template entity
/// </summary>
[Table("EmailTemplates")]
public class EmailTemplateEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Template name
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Template code (unique identifier)
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string TemplateCode { get; set; } = string.Empty;

    /// <summary>
    /// Email subject with placeholders
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Email body content (HTML)
    /// </summary>
    [Required]
    public string BodyContent { get; set; } = string.Empty;

    /// <summary>
    /// CC email addresses (comma-separated)
    /// </summary>
    [MaxLength(1000)]
    public string? CcEmails { get; set; }

    /// <summary>
    /// BCC email addresses (comma-separated)
    /// </summary>
    [MaxLength(1000)]
    public string? BccEmails { get; set; }

    /// <summary>
    /// Whether this template is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Updated timestamp
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
