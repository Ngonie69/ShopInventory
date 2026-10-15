using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Generated documents and uploaded attachments are written under the upload root and nowhere else,
/// whatever document type or entity type the request names.
/// </summary>
/// <remarks>
/// Both names used to be passed straight to <c>Path.Combine</c>, which drops everything before a
/// rooted argument: a rooted or UNC value chose the directory the API wrote into. Each test aims a
/// request at a directory beside the root and checks that nothing arrives there.
/// </remarks>
public sealed class DocumentStorageContainmentTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly string _sandbox;
    private readonly string _uploadRoot;
    private readonly string _outside;
    private readonly int _templateId;

    public DocumentStorageContainmentTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _sandbox = Path.Combine(Path.GetTempPath(), $"shopinv-contain-{Guid.NewGuid():N}");
        _uploadRoot = Path.Combine(_sandbox, "uploads");
        _outside = Path.Combine(_sandbox, "outside");
        Directory.CreateDirectory(_uploadRoot);
        Directory.CreateDirectory(_outside);

        var template = new DocumentTemplateEntity
        {
            Name = "Plain",
            DocumentType = "Invoice",
            HtmlContent = "<p>{{InvoiceNumber}}</p>"
        };
        _context.DocumentTemplates.Add(template);
        _context.SaveChanges();
        _templateId = template.Id;
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<string> HostileNames => new()
    {
        // A rooted path: Path.Combine keeps only this.
        "{outside}",
        // Climbing out of the folder the name was meant to sit in.
        "../../outside/x",
        @"..\..\outside\x",
    };

    [Theory]
    [MemberData(nameof(HostileNames))]
    public async Task A_generated_document_cannot_be_written_outside_the_upload_root(string documentType)
    {
        var result = await CreateService().GenerateDocumentAsync(
            new GenerateDocumentRequest
            {
                DocumentType = Resolve(documentType),
                EntityId = 7,
                TemplateId = _templateId,
                SaveToHistory = false
            },
            Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Empty(FilesOutsideTheRoot());
    }

    [Fact]
    public async Task A_known_document_type_is_written_under_its_canonical_name_inside_the_root()
    {
        var result = await CreateService().GenerateDocumentAsync(
            new GenerateDocumentRequest { DocumentType = "invoice", EntityId = 7, TemplateId = _templateId, SaveToHistory = false },
            Guid.NewGuid());

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("Invoice_7_", result.FileName);
        Assert.True(File.Exists(Path.Combine(_uploadRoot, "documents", result.FileName!)));
        Assert.Empty(FilesOutsideTheRoot());
    }

    [Theory]
    [MemberData(nameof(HostileNames))]
    [InlineData("Invoice/../../../outside")]
    public async Task An_attachment_cannot_be_written_outside_the_upload_root(string entityType)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 test"));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => CreateService().UploadAttachmentAsync(
            new UploadAttachmentRequest { EntityType = Resolve(entityType), EntityId = 3 },
            content,
            "note.pdf",
            "application/pdf",
            userId: null));

        Assert.Empty(FilesOutsideTheRoot());
    }

    [Fact]
    public async Task An_attachment_for_a_plain_entity_type_lands_inside_the_root_with_a_plain_extension()
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 test"));

        var attachment = await CreateService().UploadAttachmentAsync(
            new UploadAttachmentRequest { EntityType = "Invoice", EntityId = 3 },
            content,
            "note.pdf:hidden",
            "application/pdf",
            userId: null);

        var stored = Assert.Single(Directory.GetFiles(Path.Combine(_uploadRoot, "attachments", "Invoice", "3")));
        Assert.Equal(string.Empty, Path.GetExtension(stored));
        Assert.Equal("Invoice", attachment.EntityType);
        Assert.Empty(FilesOutsideTheRoot());
    }

    private string Resolve(string name) => name.Replace("{outside}", _outside);

    private string[] FilesOutsideTheRoot() =>
        Directory.GetFiles(_sandbox, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(_uploadRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private DocumentService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:UploadPath"] = _uploadRoot })
            .Build();

        // Email and SAP are not reached by generation or upload.
        return new DocumentService(
            _context,
            emailService: null!,
            sapServiceLayerClient: null!,
            NullLogger<DocumentService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            HybridLookupCacheTests.NewHybridCache(),
            configuration);
    }
}
