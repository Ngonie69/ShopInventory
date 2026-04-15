using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.IncomingPayments.Commands.CreateIncomingPayment;

public sealed class CreateIncomingPaymentHandler(
    ISAPServiceLayerClient sapClient,
    IAuditService auditService,
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

        try
        {
            var payment = await sapClient.CreateIncomingPaymentAsync(request, cancellationToken);

            logger.LogInformation("Incoming payment created successfully. DocEntry: {DocEntry}, DocNum: {DocNum}, Customer: {CardCode}, Total: {Total}",
                payment.DocEntry, payment.DocNum, payment.CardCode, payment.DocTotal);

            try { await auditService.LogAsync(AuditActions.CreatePayment, "IncomingPayment", payment.DocEntry.ToString(), $"Payment #{payment.DocNum} created for {payment.CardCode}, Total: {payment.DocTotal}", true); } catch { }

            return new IncomingPaymentCreatedResponseDto
            {
                Message = "Incoming payment created successfully",
                Payment = payment.ToDto()
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
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout or connection abort while creating incoming payment in SAP Service Layer");
            return Errors.IncomingPayment.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.IncomingPayment.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating incoming payment");
            return Errors.IncomingPayment.CreationFailed(ex.Message);
        }
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
}
