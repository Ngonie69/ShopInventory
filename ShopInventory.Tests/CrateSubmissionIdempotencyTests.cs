using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Crates.Commands.CreateCrateGrv;
using ShopInventory.Features.Crates.Commands.UploadCratePod;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A crate POD or GRV submitted from a phone can time out after the server has already committed
/// it. The unique indexes stop a second row, but not the consequences of the retry: the POD upload
/// would attach the same photo again, and the GRV would come back refused as "already exists" —
/// leaving the merchandiser told it failed with no way to reach what was raised.
/// </summary>
public sealed class CrateSubmissionIdempotencyTests : IDisposable
{
    private static readonly Guid Merchandiser = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly RecordingDocumentService _documents = new();

    public CrateSubmissionIdempotencyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = Merchandiser,
            Username = "merch",
            PasswordHash = "not-a-real-hash",
            Role = CrateTrackingConstants.SubmissionRoleMerchandiser
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Repeating_a_pod_key_replays_instead_of_attaching_the_photo_twice()
    {
        var transactionId = await GivenInvoiceTransaction(expectedQuantity: 10);
        const string key = "pod-key-1";

        var first = await UploadPodAsync(transactionId, quantity: 8, key);
        var second = await UploadPodAsync(transactionId, quantity: 8, key);

        Assert.False(first.IsError);
        Assert.False(second.IsError);
        Assert.Equal(first.Value.Id, second.Value.Id);
        Assert.Equal(1, _documents.UploadCount);
    }

    [Fact]
    public async Task A_pod_without_a_key_still_uploads_every_time()
    {
        // No key means no promise. The behaviour has to stay exactly as it was for any caller that
        // has not been updated to send one.
        var transactionId = await GivenInvoiceTransaction(expectedQuantity: 10);

        await UploadPodAsync(transactionId, quantity: 8, clientRequestId: null);
        await UploadPodAsync(transactionId, quantity: 8, clientRequestId: null);

        Assert.Equal(2, _documents.UploadCount);
    }

    [Fact]
    public async Task A_refused_pod_is_not_cached_so_a_corrected_resubmit_goes_through()
    {
        var transactionId = await GivenInvoiceTransaction(expectedQuantity: 10);
        const string key = "pod-key-2";

        var refused = await UploadPodAsync(transactionId, quantity: -1, key);
        var corrected = await UploadPodAsync(transactionId, quantity: 8, key);

        Assert.True(refused.IsError);
        Assert.False(corrected.IsError);
        Assert.Equal(1, _documents.UploadCount);
    }

    [Fact]
    public async Task Repeating_a_grv_key_replays_the_grv_rather_than_refusing_it()
    {
        var transactionId = await GivenInvoiceTransaction(expectedQuantity: 10);
        await UploadPodAsync(transactionId, quantity: 7, clientRequestId: null);
        const string key = "grv-key-1";

        var first = await CreateGrvAsync(transactionId, "short delivery", key);
        var second = await CreateGrvAsync(transactionId, "short delivery", key);

        Assert.False(first.IsError);
        Assert.False(second.IsError);
        Assert.Equal(first.Value.GrvNumber, second.Value.GrvNumber);
        Assert.Equal(-3m, second.Value.VarianceQuantity);
    }

    [Fact]
    public async Task Without_a_key_a_repeated_grv_is_still_refused_as_a_duplicate()
    {
        // The one-to-one guard is what a keyless caller falls back on, and it must stay in place.
        var transactionId = await GivenInvoiceTransaction(expectedQuantity: 10);
        await UploadPodAsync(transactionId, quantity: 7, clientRequestId: null);

        await CreateGrvAsync(transactionId, "short delivery", clientRequestId: null);
        var second = await CreateGrvAsync(transactionId, "short delivery", clientRequestId: null);

        Assert.True(second.IsError);
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_quantity_is_rejected_rather_than_silently_replayed()
    {
        // Guards the client contract: the key has to be retired when the submission changes, so a
        // stale key on new content must not quietly return the old POD.
        var transactionId = await GivenInvoiceTransaction(expectedQuantity: 10);
        const string key = "pod-key-3";

        await UploadPodAsync(transactionId, quantity: 8, key);
        var mismatch = await UploadPodAsync(transactionId, quantity: 9, key);

        Assert.True(mismatch.IsError);
        Assert.Equal("Idempotency.RequestMismatch", mismatch.FirstError.Code);
    }

    private async Task<ErrorOr.ErrorOr<CratePodSubmissionDto>> UploadPodAsync(
        int crateTransactionId,
        decimal quantity,
        string? clientRequestId)
    {
        using var context = new ApplicationDbContext(_options);
        var handler = new UploadCratePodHandler(
            context,
            _documents.AsService(),
            new NoOpAuditService(),
            BuildStore(),
            NullLogger<UploadCratePodHandler>.Instance);

        using var file = new MemoryStream([1, 2, 3]);
        return await handler.Handle(
            new UploadCratePodCommand(
                crateTransactionId,
                CrateTrackingConstants.SubmissionRoleMerchandiser,
                quantity,
                Notes: null,
                file,
                "pod.jpg",
                "image/jpeg",
                Merchandiser,
                clientRequestId),
            CancellationToken.None);
    }

    private async Task<ErrorOr.ErrorOr<CrateGrvDto>> CreateGrvAsync(
        int crateTransactionId,
        string reason,
        string? clientRequestId)
    {
        using var context = new ApplicationDbContext(_options);
        var handler = new CreateCrateGrvHandler(
            context,
            _documents.AsService(),
            new NoOpAuditService(),
            BuildStore(),
            NullLogger<CreateCrateGrvHandler>.Instance);

        using var file = new MemoryStream([1, 2, 3]);
        return await handler.Handle(
            new CreateCrateGrvCommand(
                crateTransactionId,
                reason,
                file,
                "grv.pdf",
                "application/pdf",
                Merchandiser,
                clientRequestId),
            CancellationToken.None);
    }

    /// <summary>
    /// The real store, over its own context per scope exactly as it runs in production — the point
    /// of these tests is the durable record, so a fake would prove nothing.
    /// </summary>
    private IIdempotencyRequestStore BuildStore()
        => new IdempotencyRequestStore(
            new SingleContextScopeFactory(_options),
            Options.Create(new SecuritySettings()));

    private async Task<int> GivenInvoiceTransaction(decimal expectedQuantity)
    {
        using var context = new ApplicationDbContext(_options);
        var transaction = new CrateTransactionEntity
        {
            TransactionType = CrateTrackingConstants.TransactionTypeInvoice,
            InvoiceDocEntry = Random.Shared.Next(1, int.MaxValue),
            InvoiceDocNum = 5001,
            ShopCardCode = "TMP119",
            ShopName = "Test shop",
            ExpectedQuantity = expectedQuantity,
            EffectiveDate = DateTime.UtcNow.Date,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = Merchandiser
        };

        context.CrateTransactions.Add(transaction);
        await context.SaveChangesAsync();
        return transaction.Id;
    }

    private sealed class SingleContextScopeFactory(DbContextOptions<ApplicationDbContext> options)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
            => serviceType == typeof(ApplicationDbContext) ? new ApplicationDbContext(options) : null;

        public void Dispose()
        {
        }
    }

    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null) => Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) => Task.CompletedTask;
    }

    /// <summary>
    /// Counts attachment uploads and fails the test on any other call. <see cref="IDocumentService"/>
    /// has upwards of thirty members, so it is generated rather than hand-stubbed.
    /// </summary>
    private sealed class RecordingDocumentService
    {
        private int _uploadCount;

        public int UploadCount => Volatile.Read(ref _uploadCount);

        public IDocumentService AsService()
        {
            var proxy = DispatchProxy.Create<IDocumentService, DocumentServiceProxy>();
            ((DocumentServiceProxy)proxy).Owner = this;
            return proxy;
        }

        private int RecordUpload() => Interlocked.Increment(ref _uploadCount);

        public class DocumentServiceProxy : DispatchProxy
        {
            internal RecordingDocumentService? Owner { get; set; }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod?.Name == nameof(IDocumentService.UploadAttachmentAsync))
                {
                    var id = Owner!.RecordUpload();
                    return Task.FromResult(new DocumentAttachmentDto { Id = id, FileName = "pod.jpg" });
                }

                throw new InvalidOperationException(
                    $"The crate handlers must not call {targetMethod?.Name} in these tests.");
            }
        }
    }
}
