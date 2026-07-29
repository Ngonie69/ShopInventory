using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateReservation;

public sealed class CreateReservationHandler(
    IStockReservationService reservationService,
    IIdempotencyRequestStore idempotencyRequestStore,
    ILogger<CreateReservationHandler> logger
) : IRequestHandler<CreateReservationCommand, ErrorOr<StockReservationResponseDto>>
{
    public async Task<ErrorOr<StockReservationResponseDto>> Handle(
        CreateReservationCommand command,
        CancellationToken cancellationToken)
    {
        command.Request.ExternalReferenceId = command.Request.ExternalReferenceId.Trim();

        long? idempotencyRequestId = null;
        var releaseIdempotencyRequest = false;

        try
        {
            var acquireResult = await idempotencyRequestStore.TryAcquireAsync<StockReservationResponseDto>(
                "desktop-reservations.create",
                command.Request.ExternalReferenceId,
                command.Request,
                cancellationToken);

            switch (acquireResult.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquireResult.Response is not null:
                    return acquireResult.Response;
                case IdempotencyAcquireOutcome.InProgress:
                    return Errors.Idempotency.RequestInProgress("desktop reservation creation");
                case IdempotencyAcquireOutcome.RequestMismatch:
                    return Errors.Idempotency.RequestMismatch("desktop reservation creation");
                case IdempotencyAcquireOutcome.Acquired:
                    idempotencyRequestId = acquireResult.RequestId;
                    releaseIdempotencyRequest = true;
                    break;
            }

            logger.LogInformation(
                "Desktop app creating reservation: ExternalRef={ExternalRef}, Source={Source}, Lines={LineCount}",
                command.Request.ExternalReferenceId, command.Request.SourceSystem, command.Request.Lines.Count);

            var response = await reservationService.CreateReservationAsync(
                command.Request, command.CreatedBy, cancellationToken);

            if (!response.Success && response.Errors.Any(e => e.ErrorCode == ReservationErrorCode.DuplicateReference))
            {
                var existingReservation = await reservationService.GetReservationByExternalReferenceAsync(
                    command.Request.ExternalReferenceId,
                    cancellationToken);

                if (existingReservation is not null)
                {
                    response = new StockReservationResponseDto
                    {
                        Success = true,
                        Message = "Existing reservation found for this reference",
                        Reservation = existingReservation,
                        Warnings = new List<string> { "Using existing reservation - no new reservation created" }
                    };
                }
            }

            if (!response.Success)
            {
                return Errors.DesktopIntegration.ReservationFailed(response.Message ?? "Reservation failed");
            }

            if (idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, response, cancellationToken);
                    releaseIdempotencyRequest = false;
                }
                catch (Exception completeException)
                {
                    logger.LogWarning(completeException, "Failed to persist reservation idempotency completion for request {RequestId}", idempotencyRequestId.Value);
                }
            }

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating reservation");
            return Errors.DesktopIntegration.ReservationFailed(ex.Message);
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
                    logger.LogWarning(releaseException, "Failed to release reservation idempotency request {RequestId}", idempotencyRequestId.Value);
                }
            }
        }
    }
}
