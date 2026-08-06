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
/// Covers duplicate detection for attachment uploads, which is what stops a retried POD becoming a
/// second POD.
/// </summary>
/// <remarks>
/// Duplicate detection used to key only on a caller-supplied external reference. On 2026-08-02
/// invoice 2148037 was uploaded three times in 80 seconds: the second attempt carried the same
/// reference and was correctly skipped, the third carried a different one and was stored, leaving
/// two POD attachments for one delivery. Nothing about that is visible to the uploader — both
/// requests return a valid attachment.
/// </remarks>
public sealed class PodDuplicateUploadTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly string _uploadPath;

    public PodDuplicateUploadTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _uploadPath = Path.Combine(Path.GetTempPath(), $"pod-dedupe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_uploadPath);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        try
        {
            Directory.Delete(_uploadPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>
    /// The 2148037 case: same photo, a fresh external reference on the retry.
    /// </summary>
    [Fact]
    public async Task A_re_upload_of_the_same_file_under_a_different_reference_is_not_a_second_attachment()
    {
        var service = CreateService();
        var photo = PodPhoto();

        var first = await UploadAsync(service, photo, externalReference: "MOBILE-POD-748570-FDCB9CD70399D79AE8ED344B");
        var second = await UploadAsync(service, photo, externalReference: "MOBILE-POD-748570-0B17C4419D2E5A6F3C88E107");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await CountAttachmentsAsync());
    }

    [Fact]
    public async Task A_re_upload_of_the_same_file_with_no_reference_at_all_is_not_a_second_attachment()
    {
        var service = CreateService();
        var photo = PodPhoto();

        var first = await UploadAsync(service, photo, externalReference: null);
        var second = await UploadAsync(service, photo, externalReference: null);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await CountAttachmentsAsync());
    }

    [Fact]
    public async Task A_repeat_of_the_same_reference_is_still_deduplicated()
    {
        var service = CreateService();

        var first = await UploadAsync(service, PodPhoto(), externalReference: "MOBILE-POD-748570-FDCB9CD7");
        // Different bytes, so only the reference can catch this one.
        var second = await UploadAsync(service, PodPhoto(seed: 9), externalReference: "MOBILE-POD-748570-FDCB9CD7");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await CountAttachmentsAsync());
    }

    [Fact]
    public async Task Genuinely_different_files_are_both_kept()
    {
        var service = CreateService();

        var first = await UploadAsync(service, PodPhoto(seed: 1), externalReference: null);
        var second = await UploadAsync(service, PodPhoto(seed: 2), externalReference: null);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await CountAttachmentsAsync());
    }

    [Fact]
    public async Task The_same_file_on_a_different_invoice_is_its_own_attachment()
    {
        var service = CreateService();
        var photo = PodPhoto();

        var first = await UploadAsync(service, photo, externalReference: null, entityId: 2148037);
        var second = await UploadAsync(service, photo, externalReference: null, entityId: 2148673);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await CountAttachmentsAsync());
    }

    /// <summary>
    /// Re-attaching the same document long after the fact is a deliberate act, not a retry.
    /// </summary>
    [Fact]
    public async Task The_same_file_uploaded_outside_the_duplicate_window_is_a_new_attachment()
    {
        var service = CreateService();
        var photo = PodPhoto();

        var first = await UploadAsync(service, photo, externalReference: null);
        await AgeAttachmentAsync(first.Id, TimeSpan.FromHours(4));

        var second = await UploadAsync(service, photo, externalReference: null);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await CountAttachmentsAsync());
    }

    [Fact]
    public async Task The_reused_attachment_keeps_the_stored_file_on_disk()
    {
        var service = CreateService();
        var photo = PodPhoto();

        var first = await UploadAsync(service, photo, externalReference: null);
        await UploadAsync(service, photo, externalReference: null);

        // The duplicate's freshly written file is removed, but the one the caller is handed back
        // must still be downloadable.
        var stored = await _context.Set<DocumentAttachmentEntity>()
            .AsNoTracking()
            .Where(a => a.Id == first.Id)
            .Select(a => a.StoredFileName)
            .SingleAsync();

        Assert.True(File.Exists(stored));
        Assert.Single(Directory.GetFiles(Path.Combine(_uploadPath, "attachments", "Invoice", "2148037")));
    }

    /// <summary>
    /// The last-resort guard, for the client that defeats both of the ones above it.
    /// </summary>
    /// <remarks>
    /// On 2026-08-06 the mobile app produced a differently encoded file per tap, so the content
    /// hash matched nothing while seven invoices took a second POD 2 to 10 seconds after the first.
    /// Time is the only key left when the caller varies both its reference and its bytes.
    /// <see cref="ShopInventory.Features.Invoices.Commands.UploadPod.UploadPodHandler"/> asks with a
    /// 15-second window.
    /// </remarks>
    [Fact]
    public async Task A_second_upload_by_the_same_driver_inside_the_window_finds_the_first()
    {
        var service = CreateService();
        var driver = await GivenUploaderAsync("tanaka");

        var first = await UploadAsync(service, PodPhoto(seed: 1), externalReference: null, userId: driver);

        // The handler asks before it stores, so this is the state the second tap arrives into. Its
        // bytes differ from the first and it carries no reference, so neither guard above this one
        // can see it.
        var found = await service.FindRecentAttachmentByUploaderAsync(
            "Invoice", 2148037, driver, TimeSpan.FromSeconds(15));

        Assert.NotNull(found);
        Assert.Equal(first.Id, found!.Id);
    }

    [Fact]
    public async Task An_upload_older_than_the_window_is_left_alone()
    {
        var service = CreateService();
        var driver = await GivenUploaderAsync("tanaka");

        var first = await UploadAsync(service, PodPhoto(seed: 1), externalReference: null, userId: driver);

        // A driver photographing a genuine second page cannot do it in fifteen seconds, so anything
        // this far out is a real second POD and must survive.
        await AgeAttachmentAsync(first.Id, TimeSpan.FromMinutes(9));

        var found = await service.FindRecentAttachmentByUploaderAsync(
            "Invoice", 2148037, driver, TimeSpan.FromSeconds(15));

        Assert.Null(found);
    }

    [Fact]
    public async Task A_second_driver_uploading_the_same_invoice_is_not_a_re_submission()
    {
        var service = CreateService();
        var driver = await GivenUploaderAsync("tanaka");
        var podOperator = await GivenUploaderAsync("chipo");

        await UploadAsync(service, PodPhoto(seed: 1), externalReference: null, userId: driver);

        // Two people can legitimately attach to one invoice — the POD report reports the uploaders
        // as a list precisely because that happens.
        var found = await service.FindRecentAttachmentByUploaderAsync(
            "Invoice", 2148037, podOperator, TimeSpan.FromSeconds(15));

        Assert.Null(found);
    }

    [Fact]
    public async Task A_recent_upload_on_another_invoice_is_not_a_re_submission()
    {
        var service = CreateService();
        var driver = await GivenUploaderAsync("tanaka");

        // A bulk POD run walks invoices back to back, so the previous upload is always seconds old.
        await UploadAsync(service, PodPhoto(seed: 1), externalReference: null, userId: driver, entityId: 2148037);

        var found = await service.FindRecentAttachmentByUploaderAsync(
            "Invoice", 2148673, driver, TimeSpan.FromSeconds(15));

        Assert.Null(found);
    }

    private static byte[] PodPhoto(int seed = 0)
    {
        // A PDF rather than an image: the image path re-encodes through ImageSharp, and this test
        // is about duplicate detection, not compression.
        var body = $"%PDF-1.4\nPOD scan {seed}\n%%EOF";
        return System.Text.Encoding.ASCII.GetBytes(body);
    }

    private async Task<DocumentAttachmentDto> UploadAsync(
        DocumentService service,
        byte[] content,
        string? externalReference,
        int entityId = 2148037,
        Guid? userId = null)
    {
        using var stream = new MemoryStream(content);
        return await service.UploadAttachmentAsync(
            new UploadAttachmentRequest
            {
                EntityType = "Invoice",
                EntityId = entityId,
                ExternalReference = externalReference,
                Description = "POD - Proof of Delivery",
                IncludeInEmail = false
            },
            stream,
            "POD_delivery.pdf",
            "application/pdf",
            userId);
    }

    /// <summary>An uploader row, since the attachment's UploadedByUserId is a real foreign key.</summary>
    private async Task<Guid> GivenUploaderAsync(string username)
    {
        var id = Guid.NewGuid();
        _context.Users.Add(new ShopInventory.Models.User
        {
            Id = id,
            Username = username,
            FirstName = username,
            LastName = "Tester",
            Email = $"{username}@example.com",
            PasswordHash = "x",
            Role = "Driver",
            IsActive = true
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private async Task<int> CountAttachmentsAsync() =>
        await _context.Set<DocumentAttachmentEntity>().CountAsync();

    private async Task AgeAttachmentAsync(int attachmentId, TimeSpan age)
    {
        var attachment = await _context.Set<DocumentAttachmentEntity>().SingleAsync(a => a.Id == attachmentId);
        attachment.UploadedAt = DateTime.UtcNow - age;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private DocumentService CreateService() =>
        new(
            _context,
            StubProxy.Unused<IEmailService>(),
            StubProxy.Unused<ISAPServiceLayerClient>(),
            NullLogger<DocumentService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FileStorage:UploadPath"] = _uploadPath
                })
                .Build());
}
