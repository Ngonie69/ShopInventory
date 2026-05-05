using System.Text.Json;
using ShopInventory.Models.Entities;
using ShopInventory.Models;

namespace ShopInventory.Services;

public sealed class IncomingPaymentPostingBackgroundService : BackgroundService
{
    private const string WorkerName = "incoming-payment-posting";
    private readonly IServiceProvider _serviceProvider;
    private readonly BackgroundWorkerLeaderElector _leaderElector;
    private readonly BackgroundWorkerHealthRegistry _healthRegistry;
    private readonly ILogger<IncomingPaymentPostingBackgroundService> _logger;
    private readonly TimeSpan _processingInterval = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _leadershipRetryInterval = TimeSpan.FromSeconds(5);
    private readonly int _batchSize = 5;
    private readonly SemaphoreSlim _processingSemaphore = new(1, 1);
    private int _consecutiveErrors;
    private const int MaxConsecutiveErrors = 10;

    public IncomingPaymentPostingBackgroundService(
        IServiceProvider serviceProvider,
        BackgroundWorkerLeaderElector leaderElector,
        BackgroundWorkerHealthRegistry healthRegistry,
        ILogger<IncomingPaymentPostingBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _leaderElector = leaderElector;
        _healthRegistry = healthRegistry;
        _logger = logger;
        _healthRegistry.RegisterWorker(WorkerName, critical: true, healthyWindow: TimeSpan.FromMinutes(2));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Incoming payment posting background service started - Processing every {Interval}s, Batch size: {BatchSize}",
            _processingInterval.TotalSeconds,
            _batchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var leadershipHandle = await _leaderElector.TryAcquireAsync(WorkerName, stoppingToken);
            if (leadershipHandle is null)
            {
                _healthRegistry.MarkStandby(WorkerName);
                await Task.Delay(_leadershipRetryInterval, stoppingToken);
                continue;
            }

            _healthRegistry.MarkLeader(WorkerName);
            _logger.LogInformation("Incoming payment posting leadership acquired on this instance");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await ProcessQueueAsync(stoppingToken);
                        _consecutiveErrors = 0;
                        _healthRegistry.MarkSuccessfulRun(WorkerName);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _consecutiveErrors++;
                        _healthRegistry.MarkFailure(WorkerName, ex);
                        _logger.LogError(ex, "Error in incoming payment posting background service (consecutive errors: {Count})", _consecutiveErrors);

                        if (_consecutiveErrors >= MaxConsecutiveErrors)
                        {
                            _logger.LogWarning("Too many consecutive incoming payment queue errors, backing off for 60 seconds");
                            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                            _consecutiveErrors = 0;
                        }
                    }

                    var jitter = Random.Shared.Next(-3000, 3000);
                    await Task.Delay(_processingInterval + TimeSpan.FromMilliseconds(jitter), stoppingToken);
                }
            }
            finally
            {
                _healthRegistry.MarkStandby(WorkerName);
            }
        }

        _healthRegistry.MarkStopped(WorkerName);
        _logger.LogInformation("Incoming payment posting background service stopped");
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        if (!await _processingSemaphore.WaitAsync(0, stoppingToken))
        {
            _logger.LogDebug("Incoming payment queue processing already in progress, skipping");
            return;
        }

        try
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
        finally
        {
            _processingSemaphore.Release();
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