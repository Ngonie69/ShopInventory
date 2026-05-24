using System.Text.Json;
using MediatR;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Features.Invoices.Events;

namespace ShopInventory.Services;

public sealed class InvoiceFiscalizationBackgroundService(
    IInvoiceFiscalizationQueue queue,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<InvoiceFiscalizationBackgroundService> logger) : BackgroundService
{
    private const string SourceSystem = "InvoiceFiscalisation";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Invoice fiscalization background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            InvoiceFiscalizationWorkItem workItem;

            try
            {
                workItem = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessAsync(workItem, stoppingToken);
        }

        logger.LogInformation("Invoice fiscalization background service stopped");
    }

    private async Task ProcessAsync(InvoiceFiscalizationWorkItem workItem, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var fiscalizationService = scope.ServiceProvider.GetRequiredService<IFiscalizationService>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();

            var result = await fiscalizationService.FiscalizeInvoiceAsync(
                workItem.Invoice,
                workItem.CustomerDetails,
                stoppingToken);

            var syncResult = await sender.Send(
                new SyncFiscalTransactionCommand(
                    BuildSyncRequest(workItem, result),
                    workItem.InitiatedByUserId?.ToString(),
                    workItem.InitiatedByUsername),
                stoppingToken);

            if (syncResult.IsError)
            {
                logger.LogWarning(
                    "Failed to record fiscal transaction log for invoice {DocNum}: {Errors}",
                    workItem.Invoice.DocNum,
                    string.Join("; ", syncResult.Errors.Select(error => error.Description)));
            }

            if (result.Success)
            {
                logger.LogInformation(
                    "Invoice {DocNum} fiscalized in background. QRCode: {HasQR}, ReceiptGlobalNo: {ReceiptNo}",
                    workItem.Invoice.DocNum,
                    !string.IsNullOrEmpty(result.QRCode),
                    result.ReceiptGlobalNo);
            }
            else
            {
                logger.LogWarning(
                    "Background fiscalization failed for invoice {DocNum}: {Message}",
                    workItem.Invoice.DocNum,
                    result.Message);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Background fiscalization cancelled for invoice {DocNum}", workItem.Invoice.DocNum);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background fiscalization error for invoice {DocNum}", workItem.Invoice.DocNum);
        }
    }

    private static SyncFiscalTransactionRequest BuildSyncRequest(
        InvoiceFiscalizationWorkItem workItem,
        FiscalizationResult result)
    {
        var nowUtc = DateTime.UtcNow;

        return new SyncFiscalTransactionRequest
        {
            ClientTransactionId = BuildClientTransactionId(workItem.Invoice.DocNum, nowUtc),
            TimestampUtc = nowUtc,
            DocNum = workItem.Invoice.DocNum,
            DocumentType = "Invoice",
            Status = ResolveStatus(result),
            Message = result.Message,
            VerificationCode = result.VerificationCode,
            QRCode = result.QRCode,
            DeviceSerialNumber = result.DeviceSerial,
            FiscalDay = result.FiscalDayNo,
            ReceiptGlobalNo = ParseReceiptGlobalNo(result.ReceiptGlobalNo),
            CardCode = workItem.Invoice.CardCode,
            CardName = workItem.Invoice.CardName,
            DocTotal = workItem.Invoice.DocTotal,
            VatSum = workItem.Invoice.VatSum,
            Currency = workItem.Invoice.DocCurrency,
            RawRequest = Serialize(new
            {
                Invoice = workItem.Invoice,
                CustomerDetails = workItem.CustomerDetails,
                InitiatedByUserId = workItem.InitiatedByUserId,
                InitiatedByUsername = workItem.InitiatedByUsername
            }),
            RawResponse = Serialize(result),
            SourceSystem = SourceSystem
        };
    }

    private static string ResolveStatus(FiscalizationResult result)
        => result.Skipped
            ? "Fiscalised"
            : result.Success
                ? "Success"
                : "Failed";

    private static int? ParseReceiptGlobalNo(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;

    private static string BuildClientTransactionId(int docNum, DateTime timestampUtc)
        => $"invoice-fiscalisation-{docNum}-{timestampUtc:yyyyMMddHHmmssfffffff}";

    private static string? Serialize(object? value)
        => value is null ? null : JsonSerializer.Serialize(value);
}