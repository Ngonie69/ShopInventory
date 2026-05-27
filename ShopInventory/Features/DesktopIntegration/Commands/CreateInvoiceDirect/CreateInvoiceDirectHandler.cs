using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateInvoiceDirect;

public sealed class CreateInvoiceDirectHandler(
    IStockReservationService reservationService,
    IInvoiceQueueService queueService,
    SapCircuitBreakerState sapCircuitBreakerState,
    ILogger<CreateInvoiceDirectHandler> logger
) : IRequestHandler<CreateInvoiceDirectCommand, ErrorOr<ConfirmReservationResponseDto>>
{
    public async Task<ErrorOr<ConfirmReservationResponseDto>> Handle(
        CreateInvoiceDirectCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = command.Request;
            var externalRef = request.ExternalReferenceId ??
                $"DESKTOP-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8]}";

            logger.LogInformation("Desktop app creating direct invoice: {ExternalRef}", externalRef);

            // Step 1: Create a reservation
            var reservationRequest = new CreateStockReservationRequest
            {
                ExternalReference = externalRef,
                ExternalReferenceId = externalRef,
                SourceSystem = request.SourceSystem ?? "DESKTOP_APP",
                DocumentType = ReservationDocumentType.Invoice,
                CardCode = request.CardCode,
                CardName = request.CardName,
                Currency = request.DocCurrency,
                ReservationDurationMinutes = sapCircuitBreakerState.IsOpen ? 60 : 5,
                Lines = request.Lines.Select(l => new CreateStockReservationLineRequest
                {
                    LineNum = l.LineNum,
                    ItemCode = l.ItemCode,
                    ItemDescription = l.ItemDescription,
                    Quantity = l.Quantity,
                    UoMCode = l.UoMCode,
                    WarehouseCode = l.WarehouseCode,
                    UnitPrice = l.UnitPrice ?? 0,
                    TaxCode = l.TaxCode,
                    DiscountPercent = l.DiscountPercent ?? 0,
                    CostCentreCode = l.CostCentreCode,
                    BatchNumbers = l.BatchNumbers?.Select(b => new ReservationBatchRequest
                    {
                        BatchNumber = b.BatchNumber,
                        Quantity = b.Quantity
                    }).ToList(),
                    AutoAllocateBatches = l.AutoAllocateBatches
                }).ToList()
            };

            var reservationResult = await reservationService.CreateReservationAsync(
                reservationRequest, command.CreatedBy, cancellationToken);

            if (!reservationResult.Success)
            {
                var existingReservation = await reservationService.GetReservationByExternalReferenceAsync(
                    externalRef,
                    cancellationToken);

                if (existingReservation != null
                    && string.Equals(existingReservation.Status, ReservationStatus.Confirmed, StringComparison.OrdinalIgnoreCase)
                    && existingReservation.SAPDocEntry.HasValue
                    && existingReservation.SAPDocNum.HasValue)
                {
                    logger.LogInformation(
                        "Reusing confirmed reservation {ReservationId} for duplicate invoice request {ExternalRef}. SAP DocEntry={DocEntry}, DocNum={DocNum}",
                        existingReservation.ReservationId,
                        externalRef,
                        existingReservation.SAPDocEntry.Value,
                        existingReservation.SAPDocNum.Value);

                    return new ConfirmReservationResponseDto
                    {
                        Success = true,
                        Message = "Invoice already exists for this external reference",
                        ReservationId = existingReservation.ReservationId,
                        SAPDocEntry = existingReservation.SAPDocEntry,
                        SAPDocNum = existingReservation.SAPDocNum
                    };
                }

                return Errors.DesktopIntegration.ReservationFailed(reservationResult.Message ?? "Reservation failed");
            }

            if (sapCircuitBreakerState.IsOpen)
            {
                return await QueueReservationForDeferredProcessingAsync(
                    reservationRequest,
                    reservationResult.Reservation!.ReservationId,
                    command.CreatedBy,
                    cancellationToken);
            }

            // Step 2: Immediately confirm the reservation
            var confirmRequest = new ConfirmReservationRequest
            {
                ReservationId = reservationResult.Reservation!.ReservationId,
                DocDate = request.DocDate,
                DocDueDate = request.DocDueDate,
                NumAtCard = request.NumAtCard,
                Comments = request.Comments,
                SalesPersonCode = request.SalesPersonCode,
                Fiscalize = request.Fiscalize
            };

            var confirmResult = await reservationService.ConfirmReservationAsync(confirmRequest, cancellationToken);

            if (!confirmResult.Success)
            {
                if (ShouldQueueForSapAvailabilityFailure(confirmResult))
                {
                    await reservationService.RenewReservationAsync(
                        new RenewReservationRequest
                        {
                            ReservationId = reservationResult.Reservation.ReservationId,
                            ExtensionMinutes = 60
                        },
                        cancellationToken);

                    return await QueueReservationForDeferredProcessingAsync(
                        reservationRequest,
                        reservationResult.Reservation.ReservationId,
                        command.CreatedBy,
                        cancellationToken);
                }

                // Cancel the reservation if SAP posting failed
                await reservationService.CancelReservationAsync(new CancelReservationRequest
                {
                    ReservationId = reservationResult.Reservation.ReservationId,
                    Reason = $"SAP posting failed: {string.Join(", ", confirmResult.Errors)}"
                }, cancellationToken);

                return Errors.DesktopIntegration.InvoiceCreationFailed(
                    confirmResult.Message ?? "Invoice creation failed");
            }

            return confirmResult;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating direct invoice");
            return Errors.DesktopIntegration.InvoiceCreationFailed(ex.Message);
        }
    }

    private static bool ShouldQueueForSapAvailabilityFailure(ConfirmReservationResponseDto confirmResult)
    {
        if (confirmResult.WasQueued)
        {
            return false;
        }

        if (SapFailureClassifier.ContainsAvailabilitySignal(confirmResult.Message))
        {
            return true;
        }

        return confirmResult.Errors.Any(SapFailureClassifier.ContainsAvailabilitySignal);
    }

    private async Task<ErrorOr<ConfirmReservationResponseDto>> QueueReservationForDeferredProcessingAsync(
        CreateStockReservationRequest reservationRequest,
        string reservationId,
        string? createdBy,
        CancellationToken cancellationToken)
    {
        var queueResult = await queueService.EnqueueInvoiceAsync(
            reservationRequest,
            reservationId,
            createdBy,
            cancellationToken);

        if (!queueResult.Success)
        {
            return Errors.DesktopIntegration.InvoiceCreationFailed(
                queueResult.ErrorMessage ?? "SAP is unavailable and invoice queue fallback failed");
        }

        logger.LogWarning(
            "SAP is unavailable. Reservation {ReservationId} has been queued for deferred invoice posting as queue item {QueueId}.",
            reservationId,
            queueResult.QueueId);

        return new ConfirmReservationResponseDto
        {
            Success = true,
            Message = "SAP is currently unavailable. The invoice has been queued for deferred processing.",
            ReservationId = reservationId,
            WasQueued = true,
            QueueId = queueResult.QueueId,
            QueueStatus = queueResult.Status,
            QueueExternalReference = queueResult.ExternalReference,
            EstimatedProcessingSeconds = queueResult.EstimatedProcessingTime.HasValue
                ? (int)Math.Ceiling(queueResult.EstimatedProcessingTime.Value.TotalSeconds)
                : null
        };
    }
}
