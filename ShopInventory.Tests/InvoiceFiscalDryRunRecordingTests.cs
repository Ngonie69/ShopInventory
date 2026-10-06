using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;
using ShopInventory.Features.Invoices.Events;
using ShopInventory.Features.Invoices.Queries.GetInvoiceByDocEntry;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What the two invoice paths record when the fiscalisation platform is in dry-run mode.
/// </summary>
/// <remarks>
/// The platform's dry run guards only the route these two call (<c>/api/sap/receipts/fiscalise</c>), and
/// answers <c>Skipped</c> without <c>Success</c>. Both used to test <c>Skipped</c> first and record the
/// invoice "Fiscalised": evidence of a receipt that was never filed, so the work queue dropped it and the
/// manual route refused it as already done. #675 fixed the same rule for credit memos only.
/// </remarks>
public sealed class InvoiceFiscalDryRunRecordingTests : IDisposable
{
    private static readonly FiscalizationResult DryRun = new()
    {
        Success = false,
        Skipped = true,
        ErrorCode = "DryRun",
        Message = "The fiscalisation platform is in dry-run mode; nothing was submitted to FDMS."
    };

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public InvoiceFiscalDryRunRecordingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        using var context = new ApplicationDbContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData(true, false, "Success")]
    [InlineData(true, true, "Fiscalised")] // the device already holds it
    [InlineData(false, true, "Failed")]    // a dry run: nothing was filed
    [InlineData(false, false, "Failed")]
    public void A_result_is_recorded_failure_first(bool success, bool skipped, string expected)
        => Assert.Equal(
            expected,
            FiscalTransactionStatus.Of(new FiscalizationResult { Success = success, Skipped = skipped }));

    [Fact]
    public async Task The_manual_route_records_a_dry_run_as_owing_a_receipt_and_audits_it_as_a_failure()
    {
        var invoice = new InvoiceDto { DocEntry = 2390001, DocNum = 785001, CardCode = "C001", DocTotal = 10m };
        var audited = new List<bool>();

        await using var context = new ApplicationDbContext(_options);

        var result = await new FiscalizeInvoiceHandler(
                context,
                StubProxy.For<ISender>((method, args) => args?[0] switch
                {
                    GetInvoiceByDocEntryQuery => Task.FromResult<ErrorOr<InvoiceDto>>(invoice),
                    SyncFiscalTransactionCommand command => Record(command),
                    var request => throw new InvalidOperationException(
                        $"ISender.{method.Name} was called with {request?.GetType().Name ?? "null"}.")
                }),
                FiscalisationAnswering(DryRun),
                StubProxy.For<IAuditService>((_, args) =>
                {
                    audited.Add((bool)args![4]!);
                    return Task.CompletedTask;
                }),
                Options.Create(new FiscalisationSettings()),
                NullLogger<FiscalizeInvoiceHandler>.Instance)
            .Handle(new FiscalizeInvoiceCommand(invoice.DocEntry, null, "operator"), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal([false], audited);
        await AssertOwesAReceiptAsync(invoice.DocNum);
    }

    [Fact]
    public async Task The_background_queue_records_a_dry_run_as_owing_a_receipt()
    {
        var invoice = new InvoiceDto { DocEntry = 2390002, DocNum = 785002, CardCode = "C001", DocTotal = 10m };
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddScoped(_ => new ApplicationDbContext(_options));
        services.AddScoped(_ => FiscalisationAnswering(DryRun));
        services.AddScoped(_ => StubProxy.Unused<INotificationService>());
        services.AddScoped(_ => StubProxy.For<ISender>((method, args) => args?[0] switch
        {
            SyncFiscalTransactionCommand command => RecordThen(command, recorded),
            var request => throw new InvalidOperationException(
                $"ISender.{method.Name} was called with {request?.GetType().Name ?? "null"}.")
        }));

        await using var provider = services.BuildServiceProvider();
        var queue = new InvoiceFiscalizationQueue();
        using var service = new InvoiceFiscalizationBackgroundService(
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InvoiceFiscalizationBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        // No user, so no notification: the sync is the last thing the service does with the invoice.
        Assert.True(queue.TryQueue(new InvoiceFiscalizationWorkItem(
            invoice, new CustomerFiscalDetails(), null, null, null)));
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        await AssertOwesAReceiptAsync(invoice.DocNum);
    }

    /// <summary>
    /// The stored row is read the way the fiscalisation console's work queue and the invoice list read it.
    /// </summary>
    private async Task AssertOwesAReceiptAsync(int docNum)
    {
        await using var context = new ApplicationDbContext(_options);
        var row = await context.DesktopFiscalTransactions.AsNoTracking().SingleAsync(r => r.DocNum == docNum);

        Assert.Equal("Failed", row.Status);
        Assert.Contains("dry-run", row.Message);
        Assert.False(FiscalDocumentStatusProjector.HasFiscalEvidencePredicate(row));
    }

    private static IFiscalizationService FiscalisationAnswering(FiscalizationResult result)
        => StubProxy.For<IFiscalizationService>((method, _) => method.Name switch
        {
            nameof(IFiscalizationService.FiscalizeInvoiceAsync) => Task.FromResult(result),
            _ => throw new InvalidOperationException($"IFiscalizationService.{method.Name} was not expected.")
        });

    private Task<ErrorOr<FiscalTransactionLogItemDto>> Record(SyncFiscalTransactionCommand command)
    {
        // A context of its own, as the real sender's scope would give it.
        var context = new ApplicationDbContext(_options);
        return new SyncFiscalTransactionHandler(context, NullLogger<SyncFiscalTransactionHandler>.Instance)
            .Handle(command, CancellationToken.None)
            .ContinueWith(task =>
            {
                context.Dispose();
                return task.Result;
            });
    }

    private async Task<ErrorOr<FiscalTransactionLogItemDto>> RecordThen(
        SyncFiscalTransactionCommand command, TaskCompletionSource done)
    {
        var result = await Record(command);
        done.TrySetResult();
        return result;
    }
}
