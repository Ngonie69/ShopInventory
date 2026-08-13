using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.ProblemDetails;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// An unreachable attachment store must reach the client as a retryable 503, not a bare 500.
/// </summary>
/// <remarks>
/// On 2026-08-13 the production store went away and <see cref="Directory.CreateDirectory"/> threw
/// "The network path was not found" straight out of the request pipeline: 120 POD uploads over
/// eighty minutes, a 100% failure rate, every one of them answered with an unhandled 500 that the
/// handsets could not distinguish from a permanent refusal.
///
/// The shape reproduced here is the production one. The upload root itself was reachable — the
/// service constructor creates it and never threw — so it was the <c>attachments</c> directory
/// beneath it that could not be resolved. Standing a file where that directory belongs makes
/// CreateDirectory fail on an intermediate path component exactly as the dead junction did, without
/// needing a network share in the test.
/// </remarks>
public sealed class AttachmentStorageUnavailableTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly string _uploadRoot;

    public AttachmentStorageUnavailableTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        _uploadRoot = Path.Combine(Path.GetTempPath(), $"shopinv-attach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_uploadRoot);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();

        try
        {
            Directory.Delete(_uploadRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DocumentService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:UploadPath"] = _uploadRoot
            })
            .Build();

        // Email and SAP are not reached: the upload fails on storage well before either is used.
        return new DocumentService(
            _context,
            emailService: null!,
            sapServiceLayerClient: null!,
            NullLogger<DocumentService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            configuration);
    }

    /// <summary>
    /// Blocks the "attachments" level so CreateDirectory fails on an intermediate component, which
    /// is how an unreachable junction presents.
    /// </summary>
    private void BreakAttachmentStore() =>
        File.WriteAllText(Path.Combine(_uploadRoot, "attachments"), "not a directory");

    [Fact]
    public async Task Upload_translates_an_unreachable_store_into_a_typed_storage_exception()
    {
        BreakAttachmentStore();

        var service = CreateService();
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("pod-photo-bytes"));

        var exception = await Assert.ThrowsAsync<AttachmentStorageUnavailableException>(
            () => service.UploadAttachmentAsync(
                new UploadAttachmentRequest { EntityType = "Invoice", EntityId = 2201332 },
                payload,
                "pod.jpg",
                "application/octet-stream",
                userId: null,
                CancellationToken.None));

        // The path is carried for the log line, and the original IO fault is preserved for triage.
        Assert.Contains("attachments", exception.AttachmentPath, StringComparison.Ordinal);
        Assert.IsAssignableFrom<IOException>(exception.InnerException);
    }

    [Fact]
    public async Task Upload_leaves_no_partial_file_behind_when_storage_fails()
    {
        BreakAttachmentStore();

        var service = CreateService();
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("pod-photo-bytes"));

        await Assert.ThrowsAsync<AttachmentStorageUnavailableException>(
            () => service.UploadAttachmentAsync(
                new UploadAttachmentRequest { EntityType = "Invoice", EntityId = 2201332 },
                payload,
                "pod.jpg",
                "application/octet-stream",
                userId: null,
                CancellationToken.None));

        // A failed upload must not leave an attachment row: the file is not there to serve.
        Assert.Empty(_context.Set<ShopInventory.Models.Entities.DocumentAttachmentEntity>());
    }

    [Fact]
    public async Task Handler_answers_a_storage_outage_with_a_retryable_503()
    {
        var handler = new DependencyExceptionHandler(
            NullLogger<DependencyExceptionHandler>.Instance,
            new StubProblemDetailsService());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/invoice/2201332/pod";
        httpContext.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(
            httpContext,
            new AttachmentStorageUnavailableException(
                @"C:\inetpub\ShopInventory-API\uploads\attachments\Invoice\2201332",
                new IOException("The network path was not found.")),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);

        httpContext.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(httpContext.Response.Body);
        var root = document.RootElement;

        // The handset keys its retry off this flag, so it is the load-bearing part of the response.
        Assert.True(root.GetProperty("retryable").GetBoolean());

        // The server-side path must not travel to the client.
        Assert.DoesNotContain("inetpub", root.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inetpub", root.GetProperty("title").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Negative control for the test above: a bare IOException is still not something this handler
    /// claims, which is precisely why the outage answered 500 before the storage failure was given
    /// a type of its own. If this ever starts passing the 503 test has stopped proving anything.
    /// </summary>
    [Fact]
    public async Task Handler_does_not_claim_an_untyped_io_failure()
    {
        var handler = new DependencyExceptionHandler(
            NullLogger<DependencyExceptionHandler>.Instance,
            new StubProblemDetailsService());

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(
            httpContext,
            new IOException("The network path was not found."),
            CancellationToken.None);

        Assert.False(handled);
    }

    /// <summary>
    /// Declines to write so <see cref="ProblemDetailsDefaults.WriteAsync"/> falls back to its own
    /// JSON serialisation, which is what the test asserts against.
    /// </summary>
    private sealed class StubProblemDetailsService : IProblemDetailsService
    {
        public ValueTask WriteAsync(ProblemDetailsContext context) => ValueTask.CompletedTask;

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) => ValueTask.FromResult(false);
    }
}
