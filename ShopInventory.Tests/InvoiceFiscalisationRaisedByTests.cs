using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Features.Invoices.Events;
using ShopInventory.Models;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// The invoice queue names the user who raised each invoice to the fiscalisation platform. Only the code that has just
/// posted an invoice queues it, for the user who raised it.
/// </summary>
public sealed class InvoiceFiscalisationRaisedByTests : IDisposable
{
    private static readonly Guid TatendaId = Guid.Parse("6c1f3a52-8f0e-4b8e-9d0e-2f7a3c9b1d11");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public InvoiceFiscalisationRaisedByTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

        using var context = new ApplicationDbContext(_options);
        context.Database.EnsureCreated();
        context.Users.Add(new User
        {
            Id = TatendaId,
            Username = "tatenda",
            Email = "tatenda@example.test",
            PasswordHash = "x",
            Role = "User",
            IsActive = true,
            FirstName = "Tatenda",
            LastName = "Moyo"
        });
        context.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_queued_invoice_names_the_user_who_raised_it()
    {
        var raisedBy = await FiscaliseQueuedAsync(TatendaId, "tatenda");

        Assert.Equal(new FiscalReceiptOperator("tatenda", "Tatenda Moyo"), raisedBy);
    }

    [Fact]
    public async Task A_user_found_by_username_alone_is_named_in_full()
    {
        var raisedBy = await FiscaliseQueuedAsync(userId: null, "tatenda");

        Assert.Equal(new FiscalReceiptOperator("tatenda", "Tatenda Moyo"), raisedBy);
    }

    [Fact]
    public async Task A_caller_with_no_user_row_is_named_by_its_username()
    {
        var raisedBy = await FiscaliseQueuedAsync(userId: null, "VanSalesApp");

        Assert.Equal(new FiscalReceiptOperator("VanSalesApp", null), raisedBy);
    }

    [Fact]
    public async Task An_invoice_queued_for_nobody_names_nobody()
    {
        var raisedBy = await FiscaliseQueuedAsync(userId: null, username: null);

        Assert.Null(raisedBy);
    }

    private async Task<FiscalReceiptOperator?> FiscaliseQueuedAsync(Guid? userId, string? username)
    {
        var invoice = new InvoiceDto { DocEntry = 2390010, DocNum = 798346, CardCode = "C001", DocTotal = 10m };
        var fiscalised = new TaskCompletionSource<FiscalReceiptOperator?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddScoped(_ => new ApplicationDbContext(_options));
        services.AddScoped(_ => StubProxy.For<IFiscalizationService>((method, args) =>
        {
            if (method.Name != nameof(IFiscalizationService.FiscalizeInvoiceAsync))
            {
                throw new InvalidOperationException($"IFiscalizationService.{method.Name} was not expected.");
            }

            fiscalised.TrySetResult((FiscalReceiptOperator?)args![2]);
            return Task.FromResult(new FiscalizationResult { Success = true });
        }));
        services.AddScoped(_ => StubProxy.For<ISender>((_, _) =>
            Task.FromResult<ErrorOr<FiscalTransactionLogItemDto>>(new FiscalTransactionLogItemDto())));
        services.AddScoped(_ => StubProxy.For<INotificationService>((_, _) =>
        {
            finished.TrySetResult();
            return Task.FromResult(new NotificationDto());
        }));

        await using var provider = services.BuildServiceProvider();
        var queue = new InvoiceFiscalizationQueue();
        using var service = new InvoiceFiscalizationBackgroundService(
            queue,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InvoiceFiscalizationBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.True(queue.TryQueue(new InvoiceFiscalizationWorkItem(
            invoice, new CustomerFiscalDetails(), userId, username, null)));

        var raisedBy = await fiscalised.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A work item with nobody to notify ends at the sync; one with a user ends at the notification.
        if (raisedBy is not null)
        {
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await service.StopAsync(CancellationToken.None);
        return raisedBy;
    }
}
