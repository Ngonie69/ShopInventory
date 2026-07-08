using System.Text.Json;
using Quartz;
using ShopInventory.Models.Entities;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Quartz job that posts queued incoming payments to SAP. Cadence, clustering and misfire
/// handling are owned by Quartz (see QuartzConfiguration).
/// </summary>
[DisallowConcurrentExecution]
public sealed class IncomingPaymentPostingJob : IJob
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IncomingPaymentPostingJob> _logger;
    private readonly int _batchSize = 5;

    public IncomingPaymentPostingJob(
        IServiceProvider serviceProvider,
        ILogger<IncomingPaymentPostingJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        await ProcessQueueAsync(context.CancellationToken);
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var queueService = scope.ServiceProvider.GetRequiredService<IIncomingPaymentQueueService>();
        var sapClient = scope.ServiceProvider.GetRequiredService<ISAPServiceLayerClient>();
        var auditService = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var pendingPayments = await queueService.GetNextBatchForProcessingAsync(_batchSize, stoppingToken);
        if (!pendingPayments.Any())
        {
            _logger.LogDebug("No pending incoming payments in queue");
            return;
        }

        _logger.LogInformation("Processing {Count} queued incoming payments", pendingPayments.Count);

        foreach (var queueEntry in pendingPayments)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessSinglePaymentAsync(queueEntry, queueService, sapClient, auditService, stoppingToken);
        }
    }

    private async Task ProcessSinglePaymentAsync(
        IncomingPaymentQueueEntity queueEntry,
        IIncomingPaymentQueueService queueService,
        ISAPServiceLayerClient sapClient,
        IAuditService auditService,
        CancellationToken stoppingToken)
    {
        try
        {
            await queueService.MarkAsProcessingAsync(queueEntry.Id, stoppingToken);

            var request = JsonSerializer.Deserialize<CreateIncomingPaymentRequest>(
                queueEntry.PaymentPayload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (request == null)
            {
                throw new InvalidOperationException("Failed to deserialize incoming payment payload");
            }

            var payment = await sapClient.CreateIncomingPaymentAsync(request, stoppingToken);

            await queueService.UpdateQueueEntryAsync(
                queueEntry.Id,
                IncomingPaymentQueueStatus.Completed,
                payment.DocEntry.ToString(),
                payment.DocNum,
                cancellationToken: stoppingToken);

            try
            {
                await auditService.LogAsync(
                    AuditActions.CreatePayment,
                    "IncomingPayment",
                    payment.DocEntry.ToString(),
                    $"Queued payment #{payment.DocNum} created for {payment.CardCode}, Total: {payment.DocTotal}",
                    true);
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process incoming payment: ExternalRef={ExternalReference}, QueueId={QueueId}",
                queueEntry.ExternalReference,
                queueEntry.Id);

            var retryable = SapFailureClassifier.IsTransient(ex, stoppingToken);
            var newStatus = retryable && queueEntry.RetryCount < queueEntry.MaxRetries
                ? IncomingPaymentQueueStatus.Failed
                : IncomingPaymentQueueStatus.RequiresReview;

            await queueService.UpdateQueueEntryAsync(
                queueEntry.Id,
                newStatus,
                error: ex.GetBaseException().Message,
                cancellationToken: stoppingToken);
        }
    }
}