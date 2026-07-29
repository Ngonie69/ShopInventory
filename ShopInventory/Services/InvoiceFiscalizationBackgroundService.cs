using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;
using ShopInventory.Features.Invoices.Events;
using ShopInventory.Features.Notifications;

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
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var fiscalizationService = scope.ServiceProvider.GetRequiredService<IFiscalizationService>();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
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

            await SendFiscalizationNotificationAsync(
                dbContext,
                notificationService,
                workItem,
                result,
                stoppingToken);

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
            RawRequest = result.RawRequestJson ?? Serialize(new
            {
                Invoice = workItem.Invoice,
                CustomerDetails = workItem.CustomerDetails,
                InitiatedByUserId = workItem.InitiatedByUserId,
                InitiatedByUsername = workItem.InitiatedByUsername
            }),
            RawResponse = result.RawResponseJson ?? Serialize(result),
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

    private async Task SendFiscalizationNotificationAsync(
        ApplicationDbContext dbContext,
        INotificationService notificationService,
        InvoiceFiscalizationWorkItem workItem,
        FiscalizationResult result,
        CancellationToken cancellationToken)
    {
        var username = await ResolveTargetUsernameAsync(
            dbContext,
            workItem.InitiatedByUserId,
            workItem.InitiatedByUsername,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        try
        {
            var notification = WorkflowNotificationFactory.CreateInvoiceFiscalizationNotification(
                workItem.InitiatedByUserId,
                username,
                workItem.Invoice,
                result,
                string.IsNullOrWhiteSpace(workItem.NotificationActionUrl) ? "/invoices" : workItem.NotificationActionUrl);

            await notificationService.CreateNotificationAsync(notification, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to notify user {Username} about fiscalization result for invoice {DocNum}",
                username,
                workItem.Invoice.DocNum);
        }
    }

    private static async Task<string?> ResolveTargetUsernameAsync(
        ApplicationDbContext dbContext,
        Guid? userId,
        string? username,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(username))
        {
            return username.Trim();
        }

        if (!userId.HasValue)
        {
            return null;
        }

        return await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId.Value)
            .Select(user => user.Username)
            .FirstOrDefaultAsync(cancellationToken);
    }
}