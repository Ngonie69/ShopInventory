using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.IncomingPayments.Commands.CreateIncomingPayment;

public sealed class CreateIncomingPaymentHandler(
    ISAPServiceLayerClient sapClient,
    IIncomingPaymentQueueService incomingPaymentQueueService,
    IAuditService auditService,
    INotificationService notificationService,
    SapCircuitBreakerState sapCircuitBreakerState,
    IIdempotencyRequestStore idempotencyRequestStore,
    IOptions<SAPSettings> settings,
    ILogger<CreateIncomingPaymentHandler> logger
) : IRequestHandler<CreateIncomingPaymentCommand, ErrorOr<IncomingPaymentCreatedResponseDto>>
{
    public async Task<ErrorOr<IncomingPaymentCreatedResponseDto>> Handle(
        CreateIncomingPaymentCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.IncomingPayment.SapDisabled;

        var request = command.Request;

        // Validate payment amounts
        var amountErrors = ValidatePaymentAmounts(request);
        if (amountErrors.Count > 0)
        {
            logger.LogWarning("Payment amount validation failed: {Errors}", string.Join(", ", amountErrors));
            return Errors.IncomingPayment.ValidationFailed(
                $"Amount validation failed - negative amounts are not allowed: {string.Join("; ", amountErrors)}");
        }

        var idempotencyKey = string.IsNullOrWhiteSpace(request.ClientRequestId)
            ? null
            : request.ClientRequestId.Trim();

        if (idempotencyKey is null)
            return await HandleCoreAsync(request, command.CreatedBy, cancellationToken);

        long? idempotencyRequestId = null;
        var releaseIdempotencyRequest = false;
        try
        {
            var acquireResult = await idempotencyRequestStore.TryAcquireAsync<IncomingPaymentCreatedResponseDto>(
                "incomingpayments.create",
                idempotencyKey,
                request,
                cancellationToken);

            switch (acquireResult.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquireResult.Response is not null:
                    logger.LogWarning("Replaying incoming payment creation for idempotency key {Key}", idempotencyKey);
                    return acquireResult.Response;
                case IdempotencyAcquireOutcome.InProgress:
                    return Errors.Idempotency.RequestInProgress("incoming payment creation");
                case IdempotencyAcquireOutcome.RequestMismatch:
                    return Errors.Idempotency.RequestMismatch("incoming payment creation");
                case IdempotencyAcquireOutcome.Acquired:
                    idempotencyRequestId = acquireResult.RequestId;
                    releaseIdempotencyRequest = true;
                    break;
            }

            var result = await HandleCoreAsync(request, command.CreatedBy, cancellationToken);

            // Complete on any successful terminal result (posted OR queued) so a retry replays it
            // instead of posting/queuing a duplicate.
            if (idempotencyRequestId.HasValue && !result.IsError)
            {
                try
                {
                    await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, result.Value, cancellationToken);
                    releaseIdempotencyRequest = false;
                }
                catch (Exception completeException)
                {
                    logger.LogWarning(completeException, "Failed to persist incoming payment idempotency completion for request {RequestId}", idempotencyRequestId.Value);
                }
            }

            return result;
        }
        finally
        {
            if (releaseIdempotencyRequest && idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, cancellationToken);
                }
                catch (Exception releaseException)
                {
                    logger.LogWarning(releaseException, "Failed to release incoming payment idempotency request {RequestId}", idempotencyRequestId.Value);
                }
            }
        }
    }

    private async Task<ErrorOr<IncomingPaymentCreatedResponseDto>> HandleCoreAsync(
        CreateIncomingPaymentRequest request,
        string? createdBy,
        CancellationToken cancellationToken)
    {
        if (sapCircuitBreakerState.IsOpen)
        {
            return await QueuePaymentFallbackAsync(request, createdBy, cancellationToken);
        }

        try
        {
            var payment = await sapClient.CreateIncomingPaymentAsync(request, cancellationToken);
            var paymentDto = payment.ToDto();

            logger.LogInformation("Incoming payment created successfully. DocEntry: {DocEntry}, DocNum: {DocNum}, Customer: {CardCode}, Total: {Total}",
                payment.DocEntry, payment.DocNum, payment.CardCode, payment.DocTotal);

            try { await auditService.LogAsync(AuditActions.CreatePayment, "IncomingPayment", payment.DocEntry.ToString(), $"Payment #{payment.DocNum} created for {payment.CardCode}, Total: {payment.DocTotal}", true); } catch { }

            try
            {
                var customerDisplay = BuildBusinessPartnerDisplay(paymentDto.CardCode, paymentDto.CardName);
                var totalDisplay = BuildMoneyDisplay(paymentDto.DocCurrency, paymentDto.DocTotal);

                await notificationService.CreateNotificationAsync(
                    ModuleNotificationFactory.CreateBroadcastNotification(
                        $"Incoming Payment Created: #{payment.DocNum}",
                        $"Incoming payment #{payment.DocNum} for {customerDisplay} totaling {totalDisplay} was created successfully.",
                        "Success",
                        "IncomingPayment",
                        "IncomingPayment",
                        payment.DocEntry.ToString(),
                        "/payments",
                        new Dictionary<string, string>
                        {
                            ["docEntry"] = payment.DocEntry.ToString(),
                            ["docNum"] = payment.DocNum.ToString(),
                            ["cardCode"] = paymentDto.CardCode ?? string.Empty,
                            ["cardName"] = paymentDto.CardName ?? string.Empty,
                            ["docCurrency"] = paymentDto.DocCurrency ?? string.Empty,
                            ["docTotal"] = paymentDto.DocTotal.ToString("N2")
                        }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish incoming payment notification for DocEntry {DocEntry}", payment.DocEntry);
            }

            return new IncomingPaymentCreatedResponseDto
            {
                Message = "Incoming payment created successfully",
                Payment = paymentDto
            };
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Validation error creating incoming payment");
            return Errors.IncomingPayment.ValidationFailed(ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.IncomingPayment.CreationFailed("Request was canceled by the client");
        }
        catch (Exception ex) when (SapFailureClassifier.IsTransient(ex, cancellationToken))
        {
            logger.LogWarning(ex, "SAP is unavailable while creating incoming payment. Falling back to queue.");
            return await QueuePaymentFallbackAsync(request, createdBy, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating incoming payment");
            return Errors.IncomingPayment.CreationFailed(ex.Message);
        }
    }

    private async Task<ErrorOr<IncomingPaymentCreatedResponseDto>> QueuePaymentFallbackAsync(
        CreateIncomingPaymentRequest request,
        string? createdBy,
        CancellationToken cancellationToken)
    {
        var queueResult = await incomingPaymentQueueService.EnqueuePaymentAsync(
            request,
            createdBy,
            cancellationToken);

        if (!queueResult.Success)
        {
            return Errors.IncomingPayment.CreationFailed(
                queueResult.ErrorMessage ?? "SAP is unavailable and incoming payment queue fallback failed");
        }

        return new IncomingPaymentCreatedResponseDto
        {
            Message = "SAP is currently unavailable. The incoming payment has been queued for deferred processing.",
            WasQueued = true,
            QueueId = queueResult.QueueId,
            QueueStatus = queueResult.Status,
            QueueExternalReference = queueResult.ExternalReference,
            EstimatedProcessingSeconds = queueResult.EstimatedProcessingTime.HasValue
                ? (int)Math.Ceiling(queueResult.EstimatedProcessingTime.Value.TotalSeconds)
                : null
        };
    }

    private static List<string> ValidatePaymentAmounts(CreateIncomingPaymentRequest request)
    {
        var errors = new List<string>();

        if (request.CashSum < 0) errors.Add($"Cash sum cannot be negative. Current value: {request.CashSum}");
        if (request.TransferSum < 0) errors.Add($"Transfer sum cannot be negative. Current value: {request.TransferSum}");
        if (request.CheckSum < 0) errors.Add($"Check sum cannot be negative. Current value: {request.CheckSum}");
        if (request.CreditSum < 0) errors.Add($"Credit sum cannot be negative. Current value: {request.CreditSum}");

        var totalPayment = request.CashSum + request.TransferSum + request.CheckSum + request.CreditSum;
        if (totalPayment <= 0) errors.Add("At least one payment amount must be greater than zero");

        if (request.PaymentInvoices != null)
        {
            for (int i = 0; i < request.PaymentInvoices.Count; i++)
            {
                if (request.PaymentInvoices[i].SumApplied < 0)
                    errors.Add($"Invoice {i + 1}: Sum applied cannot be negative. Current value: {request.PaymentInvoices[i].SumApplied}");
            }
        }

        if (request.PaymentChecks != null)
        {
            for (int i = 0; i < request.PaymentChecks.Count; i++)
            {
                if (request.PaymentChecks[i].CheckSum < 0)
                    errors.Add($"Check {i + 1}: Check sum cannot be negative. Current value: {request.PaymentChecks[i].CheckSum}");
            }
        }

        if (request.PaymentCreditCards != null)
        {
            for (int i = 0; i < request.PaymentCreditCards.Count; i++)
            {
                if (request.PaymentCreditCards[i].CreditSum < 0)
                    errors.Add($"Credit card {i + 1}: Credit sum cannot be negative. Current value: {request.PaymentCreditCards[i].CreditSum}");
            }
        }

        return errors;
    }

    private static string BuildBusinessPartnerDisplay(string? cardCode, string? cardName)
    {
        var normalizedCode = cardCode?.Trim();
        var normalizedName = cardName?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return normalizedCode ?? "unknown customer";
        }

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return normalizedName;
        }

        return $"{normalizedCode} - {normalizedName}";
    }

    private static string BuildMoneyDisplay(string? currency, decimal total)
        => string.IsNullOrWhiteSpace(currency)
            ? total.ToString("N2")
            : $"{currency} {total:N2}";
}
