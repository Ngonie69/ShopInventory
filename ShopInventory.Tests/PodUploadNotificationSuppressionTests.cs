using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Documents;
using ShopInventory.Features.Invoices.Commands.UploadPod;
using ShopInventory.Features.Notifications;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that a POD which turns out to be a duplicate is not announced a second time.
/// </summary>
/// <remarks>
/// The duplicate guards inside <see cref="IDocumentService.UploadAttachmentAsync"/> were invisible
/// to the caller: a reuse and a fresh store both return a valid attachment. So on 2026-08-20 two
/// requests for invoice 2229934 raced, the second correctly reused attachment 19106 — and the
/// handler went on to write a second bell entry and send a second push for a delivery that had
/// already been announced. Four of that day's twenty-four uploads were duplicates.
/// <para>
/// <see cref="AttachmentUploadOutcome"/> is what makes the reuse visible. These tests hold both
/// directions: silence on a reuse, and the announcement still happening on a genuine upload.
/// </para>
/// </remarks>
public sealed class PodUploadNotificationSuppressionTests : IDisposable
{
    private const int DocEntry = 2229934;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<CreateNotificationRequest> _notifications = [];

    public PodUploadNotificationSuppressionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_reused_attachment_raises_no_notification()
    {
        var handler = CreateHandler(wasReused: true);

        var result = await handler.Handle(NewCommand(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(19106, result.Value.Id);
        Assert.Empty(_notifications);
    }

    [Fact]
    public async Task A_genuine_upload_is_still_announced()
    {
        var handler = CreateHandler(wasReused: false);

        var result = await handler.Handle(NewCommand(), CancellationToken.None);

        Assert.False(result.IsError);
        var notification = Assert.Single(_notifications);
        Assert.Contains("POD Uploaded", notification.Title);
    }

    /// <summary>
    /// The caller still gets the attachment either way — suppressing the announcement must not turn
    /// a duplicate into an error, or the handset would retry it forever.
    /// </summary>
    [Fact]
    public async Task A_reused_attachment_is_still_returned_to_the_caller()
    {
        var handler = CreateHandler(wasReused: true);

        var result = await handler.Handle(NewCommand(), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("POD_delivery.jpg", result.Value.FileName);
    }

    private static UploadPodCommand NewCommand() =>
        new(DocEntry,
            new MemoryStream([1, 2, 3]),
            "delivery.jpg",
            "image/jpeg",
            Description: null,
            UploadedByUsername: null,
            ExternalReference: "MOBILE-POD-758051-CEB069B15F7378007C77486F",
            UserId: null);

    private UploadPodHandler CreateHandler(bool wasReused)
    {
        var attachment = new DocumentAttachmentDto
        {
            Id = 19106,
            EntityType = "Invoice",
            EntityId = DocEntry,
            FileName = "POD_delivery.jpg"
        };

        var documentService = StubProxy.For<IDocumentService>((method, _) => method.Name switch
        {
            nameof(IDocumentService.UploadAttachmentWithOutcomeAsync) =>
                Task.FromResult(new AttachmentUploadOutcome(attachment, wasReused)),

            // The handler's own pre-checks find nothing; the guard under test is the one inside the
            // upload, which only reports itself after the fact.
            nameof(IDocumentService.GetAttachmentByExternalReferenceAsync) =>
                Task.FromResult<DocumentAttachmentDto?>(null),
            nameof(IDocumentService.FindRecentAttachmentByUploaderAsync) =>
                Task.FromResult<DocumentAttachmentDto?>(null),
            nameof(IDocumentService.EnsureInvoiceCachedAsync) => Task.CompletedTask,

            _ => throw new InvalidOperationException($"Unexpected call to {method.Name}")
        });

        return new UploadPodHandler(
            StubProxy.For<ISAPServiceLayerClient>((method, _) =>
                method.Name == nameof(ISAPServiceLayerClient.GetInvoiceByDocEntryAsync)
                    ? Task.FromResult<ShopInventory.Models.Invoice?>(null)
                    : throw new InvalidOperationException($"Unexpected call to {method.Name}")),
            new DocumentAttachmentAccessService(
                _context,
                ApiKeyHttpContext(),
                StubProxy.Unused<IUserManagementService>(),
                documentService,
                NullLogger<DocumentAttachmentAccessService>.Instance),
            documentService,
            StubProxy.For<IAuthService>((method, _) =>
                method.Name == nameof(IAuthService.GetUserByUsernameAsync)
                    ? Task.FromResult<ShopInventory.Models.User?>(null)
                    : throw new InvalidOperationException($"Unexpected call to {method.Name}")),
            StubProxy.For<IAuditService>((method, _) =>
                method.Name == nameof(IAuditService.LogAsync)
                    ? Task.CompletedTask
                    : throw new InvalidOperationException($"Unexpected call to {method.Name}")),
            StubProxy.For<INotificationService>((method, args) =>
            {
                if (method.Name != nameof(INotificationService.CreateNotificationAsync))
                    throw new InvalidOperationException($"Unexpected call to {method.Name}");

                _notifications.Add((CreateNotificationRequest)args![0]!);
                return Task.FromResult(new NotificationDto());
            }),
            NullLogger<UploadPodHandler>.Instance);
    }

    /// <summary>
    /// An API-key principal, which is how every POD upload in the production log reached this
    /// handler ("API key authentication successful for: MainIntegration"). It bypasses the
    /// per-entity access checks, leaving the duplicate handling as the thing under test.
    /// </summary>
    private static IHttpContextAccessor ApiKeyHttpContext()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.AuthenticationMethod, "ApiKey"),
                new Claim(ClaimTypes.Role, "Admin")
            ],
            authenticationType: "ApiKey");

        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }
}
