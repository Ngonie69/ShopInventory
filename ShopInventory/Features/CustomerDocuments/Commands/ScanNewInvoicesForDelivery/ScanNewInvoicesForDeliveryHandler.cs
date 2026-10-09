using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Commands.ScanNewInvoicesForDelivery;

/// <summary>
/// The producer of automatic sends: reads SAP's new invoices past a DocEntry watermark and queues one
/// delivery per invoice and automatic number of its customer.
/// </summary>
/// <remarks>
/// <para>
/// A scan rather than a hook in each posting path, because invoices reach SAP from seven places in
/// this system and from people keying them straight into B1, which no hook here can see. It also
/// leaves the invoice posting paths exactly as they were.
/// </para>
/// <para>
/// The first pass only records where SAP is, so no invoice from before the scan existed is ever sent.
/// Each pass after that re-reads a few DocEntries under the watermark, because two posts can finish out
/// of order; the unique index on automatic rows, and the check against it here, keep the overlap from
/// queuing anything twice. With automatic sending off the watermark still moves, so turning it on
/// sends from that moment and never floods the customers with the backlog.
/// </para>
/// <para>
/// Queuing is all this does. Whether and when a row goes — the fiscal check, the window, the caps, the
/// per-number switch at the moment of sending — is the delivery job's, as for every other send. The
/// rows and the new watermark are saved together, so a pass that fails leaves both as they were.
/// </para>
/// </remarks>
public sealed class ScanNewInvoicesForDeliveryHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings,
    IOptions<CustomerDocumentDeliverySettings> options,
    IOptions<FiscalisationSettings> fiscalisationOptions,
    ICustomerDocumentDispatchTrigger dispatchTrigger,
    ICustomerDocumentPacer pacer,
    ILogger<ScanNewInvoicesForDeliveryHandler> logger)
    : IRequestHandler<ScanNewInvoicesForDeliveryCommand, ErrorOr<ScanNewInvoicesForDeliveryResult>>
{
    private const string ScanActor = "Automatic invoice scan";

    public async Task<ErrorOr<ScanNewInvoicesForDeliveryResult>> Handle(
        ScanNewInvoicesForDeliveryCommand command,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;

        if (!settings.Enabled || !sapSettings.Value.Enabled)
            return new ScanNewInvoicesForDeliveryResult("Switched off", 0, 0, 0, null);

        if (command.SapHeldBack)
            return new ScanNewInvoicesForDeliveryResult("SAP held back", 0, 0, 0, null);

        var now = pacer.UtcNow;
        var checkpoint = await InvoiceScanCheckpoint.ReadAsync(context, cancellationToken);

        if (checkpoint is null)
            return await InitialiseAsync(now, cancellationToken);

        var invoices = await ReadNewInvoicesAsync(checkpoint.LastDocEntry, settings, cancellationToken);
        var runtime = await CustomerDocumentDeliveryKeys.ReadAsync(context, cancellationToken);

        var queued = 0;
        var skipped = 0;
        if (runtime.AutoSendEnabled && invoices.Count > 0)
        {
            (queued, skipped) = await StageDeliveriesAsync(invoices, settings, now, cancellationToken);
        }

        checkpoint.LastDocEntry = Math.Max(checkpoint.LastDocEntry, invoices.Count == 0 ? 0 : invoices.Max(invoice => invoice.DocEntry));
        checkpoint.LastScanAtUtc = now;
        await checkpoint.StageAsync(context, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        if (queued > 0)
        {
            logger.LogInformation(
                "Invoice scan queued {Queued} automatic WhatsApp send(s) and skipped {Skipped}, up to DocEntry {DocEntry}",
                queued, skipped, checkpoint.LastDocEntry);
            await dispatchTrigger.TriggerAsync(cancellationToken);
        }

        var outcome = runtime.AutoSendEnabled ? "Scanned" : "Scanned, automatic sending off";
        return new ScanNewInvoicesForDeliveryResult(outcome, invoices.Count, queued, skipped, checkpoint.LastDocEntry);
    }

    private async Task<ScanNewInvoicesForDeliveryResult> InitialiseAsync(DateTime now, CancellationToken cancellationToken)
    {
        var latest = await sapClient.GetLatestInvoiceDocEntryAsync(cancellationToken) ?? 0;

        var checkpoint = new InvoiceScanCheckpoint
        {
            LastDocEntry = latest,
            InitialisedAtUtc = now,
            LastScanAtUtc = now
        };
        await checkpoint.StageAsync(context, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Invoice scan started at DocEntry {DocEntry}; invoices posted before now are never sent automatically",
            latest);

        return new ScanNewInvoicesForDeliveryResult("Started", 0, 0, 0, latest);
    }

    private async Task<List<Invoice>> ReadNewInvoicesAsync(
        int lastDocEntry,
        CustomerDocumentDeliverySettings settings,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(settings.InvoiceScanPageSize, 1, 500);
        var maxPages = Math.Max(1, settings.InvoiceScanMaxPagesPerPass);
        var after = Math.Max(0, lastDocEntry - Math.Max(0, settings.InvoiceScanOverlap));
        var invoices = new List<Invoice>();

        for (var page = 0; page < maxPages; page++)
        {
            var batch = await sapClient.GetInvoiceDeliveryHeadersAfterDocEntryAsync(after, pageSize, cancellationToken);
            invoices.AddRange(batch);

            if (batch.Count < pageSize)
                break;

            after = batch.Max(invoice => invoice.DocEntry);
        }

        return invoices
            .GroupBy(invoice => invoice.DocEntry)
            .Select(group => group.First())
            .OrderBy(invoice => invoice.DocEntry)
            .ToList();
    }

    private async Task<(int Queued, int Skipped)> StageDeliveriesAsync(
        List<Invoice> invoices,
        CustomerDocumentDeliverySettings settings,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var cardCodes = invoices
            .Select(invoice => Clean(invoice.CardCode))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (cardCodes.Count == 0)
            return (0, 0);

        // Only numbers on an account customer's card. A route customer's numbers belong to sales made
        // under a van's card, which this scan does not send.
        var contacts = await context.CustomerWhatsAppContacts
            .AsNoTracking()
            .Where(contact => contact.CardCode != null
                && cardCodes.Contains(contact.CardCode)
                && contact.RouteCustomerId == null
                && contact.RemovedAtUtc == null
                && contact.OptedOutAtUtc == null
                && contact.AutoSendInvoices)
            .ToListAsync(cancellationToken);

        if (contacts.Count == 0)
            return (0, 0);

        var contactsByCard = contacts
            .GroupBy(contact => contact.CardCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var candidates = invoices
            .Where(invoice => Clean(invoice.CardCode) is { } card && contactsByCard.ContainsKey(card))
            .ToList();

        if (candidates.Count == 0)
            return (0, 0);

        var candidateDocEntries = candidates.Select(invoice => (int?)invoice.DocEntry).ToList();
        var alreadyQueued = (await context.CustomerDocumentDeliveries
                .AsNoTracking()
                .Where(delivery => delivery.Trigger == CustomerDocumentDeliveryTrigger.Auto
                    && candidateDocEntries.Contains(delivery.SapDocEntry))
                .Select(delivery => new { delivery.SapDocEntry, delivery.RecipientE164 })
                .ToListAsync(cancellationToken))
            .Select(row => (row.SapDocEntry!.Value, row.RecipientE164))
            .ToHashSet();

        var sellingAccounts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var todayCat = AuditService.ToCAT(now).Date;
        var queued = 0;
        var skipped = 0;

        foreach (var invoice in candidates)
        {
            var cardCode = Clean(invoice.CardCode)!;
            var recipients = contactsByCard[cardCode]
                .Where(contact => !alreadyQueued.Contains((invoice.DocEntry, contact.PhoneE164)))
                .GroupBy(contact => contact.PhoneE164, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            if (recipients.Count == 0)
                continue;

            if (!sellingAccounts.TryGetValue(cardCode, out var isSellingAccount))
            {
                isSellingAccount = await SellingAccountCards.IsSellingAccountAsync(context, cardCode, cancellationToken);
                sellingAccounts[cardCode] = isSellingAccount;
            }

            var decision = InvoiceDeliveryClassifier.Classify(
                invoice,
                isConsolidated: await InvoiceDeliveryRules.IsConsolidatedAsync(context, invoice, cancellationToken),
                isSellingAccount,
                isReposted: InvoiceDeliveryRules.IsReposted(fiscalisationOptions.Value, invoice),
                todayCat,
                settings);

            if (decision.Kind == InvoiceDeliveryDecisionKind.Ignore)
            {
                logger.LogDebug("Invoice {DocNum} not sent automatically: {Reason}", invoice.DocNum, decision.Reason);
                continue;
            }

            foreach (var contact in recipients)
            {
                var delivery = NewDelivery(invoice, cardCode, contact, now);

                if (decision.Kind == InvoiceDeliveryDecisionKind.Skip)
                {
                    delivery.Status = CustomerDocumentDeliveryStatus.Skipped;
                    delivery.StatusReason = CustomerDocumentDeliveryRules.Truncate(decision.Reason, 500);
                    delivery.ClosedAtUtc = now;
                    delivery.ClosedBy = ScanActor;
                    skipped++;
                }
                else
                {
                    queued++;
                }

                context.CustomerDocumentDeliveries.Add(delivery);
            }
        }

        return (queued, skipped);
    }

    private static CustomerDocumentDeliveryEntity NewDelivery(
        Invoice invoice,
        string cardCode,
        CustomerWhatsAppContactEntity contact,
        DateTime now) => new()
    {
        DocumentType = CustomerDocumentType.SapInvoice,
        SapDocEntry = invoice.DocEntry,
        SapDocNum = invoice.DocNum,
        DocumentNumber = invoice.DocNum.ToString(CultureInfo.InvariantCulture),
        SaleReference = Clean(invoice.U_Van_saleorder),
        DocumentDate = InvoiceDeliveryRules.ParseDocDate(invoice.DocDate),
        DocumentTotal = invoice.DocTotal,
        DocumentTotalFc = InvoiceDeliveryRules.ForeignTotal(invoice),
        Currency = Clean(invoice.DocCurrency),
        CardCode = cardCode,
        CardName = CustomerDocumentDeliveryRules.Truncate(Clean(invoice.CardName), 200),
        ContactId = contact.Id,
        RecipientE164 = contact.PhoneE164,
        RecipientName = CustomerDocumentDeliveryRules.Truncate(contact.ContactName ?? contact.OwnerName, 100),
        Trigger = CustomerDocumentDeliveryTrigger.Auto,
        ConsentAffirmed = false,
        RequestedBy = ScanActor,
        Priority = 0,
        Status = CustomerDocumentDeliveryStatus.Pending,
        NextAttemptAtUtc = now,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
