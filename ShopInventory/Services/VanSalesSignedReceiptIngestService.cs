using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Services;

/// <summary>
/// Hands the fiscalisation platform the receipts a van handset signed for itself, so that ZIMRA is
/// eventually given them.
///
/// A receipt stamped offline exists in three places and belongs in a fourth: on the handset, in the
/// customer's hand, and on the SAP invoice posted that night — but until the platform has archived it, it
/// is in no fiscal day file, and the day closes reporting fewer receipts than the van actually printed.
/// The handset consumed those numbers off the device's own sequence, so the gap is not cosmetic: FDMS
/// reconciles a fiscal day against a contiguous chain.
///
/// Two rules govern everything here, and both come from the chain rather than from taste:
///
///  1. <b>Strictly in signing order, per device.</b> The platform accepts receipt N+1 only once it holds
///     N, because each receipt is signed against its predecessor's hash. Receipts are therefore walked in
///     ascending global number within a device.
///  2. <b>A failure stops that device, it never skips.</b> Moving on to the next receipt after a failure
///     would ask the platform to accept a receipt whose predecessor it does not have, turning one
///     transient error into a chain break for the rest of the day. Other devices are unaffected — a van
///     is one chain, and one van's problem must not hold up another's takings.
/// </summary>
public sealed class VanSalesSignedReceiptIngestService(
    ApplicationDbContext context,
    IFiscalisationApiClient fiscalisationClient,
    IFiscalDeviceConfigCache deviceConfigCache,
    IOptions<FiscalisationSettings> fiscalisationOptions,
    ILogger<VanSalesSignedReceiptIngestService> logger)
{
    /// <summary>
    /// After this many failures a receipt stops being retried and waits for a person. It is a higher bar
    /// than the SAP posting job's because the consequence is heavier: this receipt blocks every later
    /// receipt from the same handset, so giving up on it stops that van's whole fiscal day.
    /// </summary>
    internal const int MaxIngestAttempts = 8;

    /// <summary>
    /// The platform's code for "your chain and mine have diverged". It is a 409, not a validation error,
    /// and it is the one failure that must never be retried — the receipt cannot be re-signed, because the
    /// signature is chained and amending one invalidates every receipt after it.
    /// </summary>
    private const string ChainBreakErrorCode = "ChainBreak";

    public async Task<VanSalesReceiptIngestRunResult> IngestPendingReceiptsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new VanSalesReceiptIngestRunResult();

        if (!fiscalisationOptions.Value.Enabled)
        {
            logger.LogDebug("Fiscalisation is switched off, so no signed van receipts were submitted.");
            return result;
        }

        // Everything still outstanding, including the receipts that can no longer be sent. Loading the
        // blocked ones deliberately: filtering them out here would let the walk below step over a hole in
        // a device's chain and offer the platform a receipt whose predecessor it does not hold.
        //
        // Unstamped sales are the one exclusion, and it is the opposite case. They were never signed, so
        // they hold no position in any chain and nothing waits behind them; treating one as a hole would
        // stop a device that has nothing wrong with it.
        // Both van sources, and that is not a widening for tidiness. A handset owns one device and stamps
        // every sale off its one chain, so a sale it happened to make with signal sits in the same
        // sequence as the ones it made without — and the platform accepts receipt N+1 only once it holds
        // N. Draining one source and not the other would stop the device at the first online sale of the
        // day and leave everything behind it unarchivable.
        var pending = await context.DesktopSales
            .Include(sale => sale.Lines)
            .Where(sale => sale.SourceSystem != null &&
                           SaleSourceSystems.VanSaleSources.Contains(sale.SourceSystem) &&
                           sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.NotApplicable &&
                           sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Ingested &&
                           sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Unstamped)
            .OrderBy(sale => sale.ReceiptGlobalNo)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return result;
        }

        // One chain per device, so the work is grouped by device and each group is walked in the order
        // the handset signed them. A sale with no device is its own group: it cannot be submitted, and
        // grouping the unattributable ones together keeps them from stopping a device that is fine.
        var byDevice = pending
            .GroupBy(sale => sale.FiscalDeviceId ?? 0)
            .OrderBy(group => group.Key);

        logger.LogInformation(
            "Submitting {Count} signed van receipt(s) across {DeviceCount} device(s) to the fiscalisation platform.",
            pending.Count,
            byDevice.Count());

        foreach (var device in byDevice)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await IngestDeviceAsync(device.Key, device.OrderBy(sale => sale.ReceiptGlobalNo).ToList(), result, cancellationToken);

            // Saved per device rather than once at the end: a run that dies half way should keep the
            // devices it already finished, and each device's rows are independent of the others'.
            await context.SaveChangesAsync(cancellationToken);

            // A missing route is not this device's problem, it is every device's. Trying the rest would
            // add one identical failure per handset to the log and change nothing.
            if (result.PlatformEndpointMissing)
            {
                break;
            }
        }

        logger.LogInformation(
            "Signed van receipt ingest finished: {Ingested} archived, {Failed} failed, {ChainBroken} chain breaks, {Stopped} device(s) stopped.",
            result.Ingested,
            result.Failed,
            result.ChainBroken,
            result.DevicesStopped);

        return result;
    }

    private async Task IngestDeviceAsync(
        int deviceNumber,
        List<DesktopSaleEntity> receipts,
        VanSalesReceiptIngestRunResult result,
        CancellationToken cancellationToken)
    {
        // Read once per device rather than per receipt: it is the same answer for every receipt in this
        // group, and it is only used to judge them. A device whose configuration cannot be read still
        // has its receipts offered — the preflight simply checks less, and the platform checks it all
        // again on arrival.
        var deviceConfig = deviceNumber > 0
            ? await deviceConfigCache.TryGetAsync(deviceNumber, cancellationToken)
            : null;

        foreach (var sale in receipts)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // A receipt that can no longer be sent is the end of this device's queue, not a row to step
            // over. Its number was spent on the handset, so the platform will never accept anything
            // numbered after it until a person resolves the gap.
            if (IsBlocked(sale))
            {
                result.DevicesStopped++;

                logger.LogInformation(
                    "Device {DeviceNumber} is held at van receipt {Reference} ({Status}), so its remaining " +
                    "{Waiting} receipt(s) were not offered. This needs a person: {Error}",
                    deviceNumber,
                    sale.ExternalReferenceId,
                    sale.ReceiptIngestStatus,
                    receipts.Count - receipts.IndexOf(sale) - 1,
                    sale.ReceiptIngestError);
                return;
            }

            var request = TryBuildRequest(sale, out var buildFailure);
            if (request is null)
            {
                // Nothing about this row can be fixed by retrying it, and the receipts behind it can never
                // be accepted while it is missing from the chain.
                MarkUnsignable(sale, buildFailure!);
                result.Unsignable++;
                result.DevicesStopped++;
                result.Errors.Add($"{sale.ExternalReferenceId}: {buildFailure}");

                logger.LogError(
                    "Van receipt {Reference} on device {DeviceNumber} cannot be submitted: {Reason} " +
                    "Every later receipt from this handset is blocked until it is resolved.",
                    sale.ExternalReferenceId,
                    deviceNumber,
                    buildFailure);
                return;
            }

            var refusal = await PreflightAsync(request, deviceConfig, cancellationToken);
            if (refusal is not null)
            {
                // Everything a preflight can find on a signed receipt is permanent. The payload cannot be
                // adjusted, because adjusting it invalidates the signature it was signed with, so there
                // is no version of this receipt that would be accepted. Marking it now spends none of its
                // attempts and says exactly which rule it breaks, rather than leaving the device stopped
                // behind eight identical rejections.
                MarkUnsignable(sale, refusal);
                result.Unsignable++;
                result.DevicesStopped++;
                result.Errors.Add($"{sale.ExternalReferenceId}: {refusal}");

                logger.LogError(
                    "Van receipt {Reference} on device {DeviceNumber} fails preflight and can never be " +
                    "accepted: {Reason} Every later receipt from this handset is blocked until it is resolved.",
                    sale.ExternalReferenceId,
                    deviceNumber,
                    refusal);
                return;
            }

            try
            {
                // The platform replays an already-archived receipt rather than duplicating it, so losing
                // this response — to a cancellation, a timeout, a restart — costs a retry and nothing else.
                var response = await fiscalisationClient.IngestSignedReceiptAsync(request, cancellationToken);

                sale.ReceiptIngestStatus = DesktopSaleReceiptIngestStatus.Ingested;
                sale.ReceiptIngestedAt = DateTime.UtcNow;
                sale.PlatformReceiptId = response.ReceiptId;
                sale.ReceiptIngestError = null;
                result.Ingested++;

                logger.LogInformation(
                    "Archived van receipt {Reference} (device {DeviceId}, fiscal day {FiscalDayNo}, receipt {GlobalNo}/{Counter}) at the fiscalisation platform.",
                    sale.ExternalReferenceId,
                    request.DeviceId,
                    request.FiscalDayNo,
                    request.ReceiptGlobalNo,
                    request.ReceiptCounter);
            }
            catch (FiscalisationApiException ex) when (
                string.Equals(ex.ErrorCode, ChainBreakErrorCode, StringComparison.OrdinalIgnoreCase))
            {
                await MarkChainBrokenAsync(sale, deviceNumber, ex, cancellationToken);
                result.ChainBroken++;
                result.DevicesStopped++;
                result.Errors.Add($"{sale.ExternalReferenceId}: {ex.Message}");
                return;
            }
            catch (FiscalisationApiException ex) when (IsEndpointMissing(ex))
            {
                // The platform in front of us has no ingest-signed route: this build predates it. That is
                // an environment problem, not a bad receipt, so it must not spend the receipt's attempts —
                // eight of those at two-minute intervals would block every handset inside a quarter of an
                // hour, and each one would then need a person to reset it once the platform is updated.
                sale.ReceiptIngestError = Truncate(
                    "The fiscalisation platform does not serve /api/receipts/ingest-signed. This receipt " +
                    "is untouched and will be submitted once the platform is updated.",
                    2000);

                result.PlatformEndpointMissing = true;
                result.Errors.Add($"{sale.ExternalReferenceId}: {ex.Message}");

                logger.LogError(
                    ex,
                    "The fiscalisation platform has no /api/receipts/ingest-signed route (HTTP {StatusCode}), so no " +
                    "van receipt can be submitted. Nothing has been marked failed and no attempts were spent; the " +
                    "receipts wait as they are. The platform must be updated — until then every fiscal day these " +
                    "vans trade in will close without their receipts.",
                    (int)ex.StatusCode);

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sale.ReceiptIngestAttempts++;
                sale.ReceiptIngestStatus = DesktopSaleReceiptIngestStatus.Failed;
                sale.ReceiptIngestError = Truncate(ex.Message, 2000);
                result.Failed++;
                result.DevicesStopped++;
                result.Errors.Add($"{sale.ExternalReferenceId}: {ex.Message}");

                var exhausted = sale.ReceiptIngestAttempts >= MaxIngestAttempts;

                logger.Log(
                    exhausted ? LogLevel.Error : LogLevel.Warning,
                    ex,
                    "Could not submit van receipt {Reference} on device {DeviceNumber} (attempt {Attempt} of {Max}). " +
                    "{Consequence}",
                    sale.ExternalReferenceId,
                    deviceNumber,
                    sale.ReceiptIngestAttempts,
                    MaxIngestAttempts,
                    exhausted
                        ? "It will not be retried automatically, and this handset's later receipts stay blocked behind it."
                        : "The rest of this handset's receipts wait behind it until the next run.");

                return;
            }
        }
    }

    /// <summary>
    /// Checks a receipt against everything known to refuse it, and returns why if anything does.
    /// </summary>
    /// <remarks>
    /// Preflight normally exists to prevent a bad submission. Here it cannot — the receipt was signed
    /// hours ago on a handset and is in a customer's hand — so what it buys is a precise diagnosis
    /// instead of a stopped van and eight identical rejections in the log. That is worth more than it
    /// sounds: a device holds its whole queue behind its first failure, so the difference between
    /// "RCPT025: line 2 uses tax id 3, which this device does not have" and a masked platform error is
    /// the difference between a fix this afternoon and a fix tomorrow.
    ///
    /// Only outright refusals are returned. A warning is left to the log, because a receipt that will be
    /// accepted must be offered — holding it back to make a point strands every receipt behind it.
    /// </remarks>
    private async Task<string?> PreflightAsync(
        IngestSignedReceiptApiRequest request,
        FiscalConfigApiResponse? deviceConfig,
        CancellationToken cancellationToken)
    {
        var settings = fiscalisationOptions.Value;

        var local = ReceiptPreflight.InspectSigned(
            request,
            new PreflightContext(
                deviceConfig,
                // The receipt's own clock, not this server's. Judging a receipt signed at 19:40 in the
                // taxpayer's time against a UTC now would put every evening receipt outside its fiscal
                // day by the offset.
                NowLocal: request.ReceiptDate ?? DateTime.Now,
                FiscalDayOpenedAt: request.FiscalDayOpenedAt == default ? null : request.FiscalDayOpenedAt,
                WarnAtPercentOfMaxHrs: settings.FiscalDay.WarnAtPercentOfMaxHrs));

        foreach (var warning in local.Warnings)
        {
            logger.LogWarning(
                "Van receipt {InvoiceNo} on device {DeviceId}: {Warning}",
                request.InvoiceNo,
                request.DeviceId,
                warning);
        }

        if (local.IsBlocked)
        {
            return local.BlockSummary;
        }

        if (settings.Preflight.Mode != FiscalisationPreflightMode.LocalAndPlatform)
        {
            return null;
        }

        try
        {
            var platform = await fiscalisationClient.PreflightSignedReceiptAsync(request, cancellationToken);

            return platform.Valid || platform.Failures.Count == 0
                ? null
                : string.Join(" ", platform.Failures);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FiscalisationApiException ex) when (IsEndpointMissing(ex))
        {
            // A platform older than the preflight route. The receipt is unaffected — submit it and let
            // the platform judge it on arrival, which is what happened before this check existed.
            logger.LogDebug(
                "The fiscalisation platform does not serve the preflight route, so van receipt {InvoiceNo} " +
                "was checked locally only.",
                request.InvoiceNo);

            return null;
        }
        catch (Exception ex)
        {
            // A preflight is an accuracy measure, not an authorisation step. Turning its outage into a
            // fiscalisation outage would stop every van over a check whose answer the platform gives
            // again, for free, when the receipt arrives.
            logger.LogWarning(
                ex,
                "Could not preflight van receipt {InvoiceNo} with the platform, so it was checked locally " +
                "only. {Consequence}",
                request.InvoiceNo,
                settings.Preflight.FailClosed
                    ? "Fiscalisation:Preflight:FailClosed is set, so it is being held back."
                    : "It is being submitted anyway; the platform validates it again on arrival.");

            return settings.Preflight.FailClosed
                ? $"The platform preflight could not be reached ({ex.Message}) and Preflight:FailClosed is set."
                : null;
        }
    }

    /// <summary>
    /// Whether this receipt can never be sent again without someone intervening — a chain break, a
    /// missing signature, or a receipt that has used up its attempts. All three stop the device rather
    /// than the receipt, because nothing behind them can be accepted while they are missing.
    /// </summary>
    /// <summary>
    /// Whether the platform did not recognise the route at all, as opposed to refusing what was sent.
    ///
    /// The distinction decides whether a receipt's attempts are spent, so it is drawn narrowly. A
    /// platform build that has the route always explains itself with a problem document — even its
    /// rejections carry an error code — while one that does not have it never reaches that middleware and
    /// answers with an empty body. A 404 says the same thing more plainly, and would be what a proxy or a
    /// mis-set base URL produces.
    /// </summary>
    private static bool IsEndpointMissing(FiscalisationApiException failure) =>
        failure.StatusCode == HttpStatusCode.NotFound
        || (failure.StatusCode == HttpStatusCode.BadRequest
            && failure.ErrorCode is null
            && !failure.HasProblemDocument);

    private static bool IsBlocked(DesktopSaleEntity sale) =>
        sale.ReceiptIngestStatus is DesktopSaleReceiptIngestStatus.ChainBroken
            or DesktopSaleReceiptIngestStatus.Unsignable
        || sale.ReceiptIngestAttempts >= MaxIngestAttempts;

    /// <summary>
    /// Records a chain break and says, in the row itself, what it means: this device is stopped until a
    /// person reconciles it, and resending cannot help.
    /// </summary>
    private async Task MarkChainBrokenAsync(
        DesktopSaleEntity sale,
        int deviceNumber,
        FiscalisationApiException failure,
        CancellationToken cancellationToken)
    {
        sale.ReceiptIngestStatus = DesktopSaleReceiptIngestStatus.ChainBroken;
        sale.ReceiptIngestError = Truncate(failure.Message, 2000);

        // A receipt that arrived with no signature is the most likely reason the chain has a hole in it:
        // its number was spent on the handset and can never be archived. Worth naming here rather than
        // leaving whoever reads the alert to discover it.
        var unsignableAhead = await context.DesktopSales
            .CountAsync(
                other => other.SourceSystem != null &&
                         SaleSourceSystems.VanSaleSources.Contains(other.SourceSystem) &&
                         other.FiscalDeviceId == sale.FiscalDeviceId &&
                         other.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unsignable &&
                         other.ReceiptGlobalNo < sale.ReceiptGlobalNo,
                cancellationToken);

        logger.LogError(
            "Fiscal chain break on device {DeviceNumber} at van receipt {Reference} (receipt {GlobalNo}): {Detail} " +
            "This handset's receipts are refused from here on and its fiscal day cannot be packaged until the " +
            "chain is reconciled. Do not resend. {UnsignableNote}",
            deviceNumber,
            sale.ExternalReferenceId,
            sale.ReceiptGlobalNo,
            failure.Message,
            unsignableAhead > 0
                ? $"{unsignableAhead} earlier receipt(s) from this handset arrived without a signature and can never be archived, which would explain the break."
                : "No earlier receipt from this handset is missing a signature, so the divergence started elsewhere.");
    }

    /// <summary>
    /// Builds the platform request from what was stored, or explains why the receipt can never be sent.
    /// Nothing here is defaulted or re-derived: every value was covered by the handset's signature, and a
    /// substitute would be refused as a tampered payload — correctly.
    /// </summary>
    private static IngestSignedReceiptApiRequest? TryBuildRequest(DesktopSaleEntity sale, out string? failure)
    {
        if (sale.FiscalDeviceId is not > 0)
        {
            failure = "the sale names no fiscal device, and a pre-signed receipt belongs to exactly one device's chain.";
            return null;
        }

        var deviceId = sale.FiscalDeviceId.Value;

        if (!int.TryParse(sale.FiscalDayNo?.Trim(), out var fiscalDayNo) || fiscalDayNo <= 0)
        {
            failure = "the sale names no fiscal day, which the platform cannot infer for a day it never opened.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(sale.DeviceSignatureHash) ||
            string.IsNullOrWhiteSpace(sale.DeviceSignatureValue))
        {
            failure = "it carries no device signature, so there is nothing for the platform to verify.";
            return null;
        }

        if (sale.ReceiptDate is null || sale.ReceiptDate.Value == default)
        {
            failure = "it carries no receipt date, which is part of the signed payload.";
            return null;
        }

        if (sale.FiscalDayOpenedAt is null || sale.FiscalDayOpenedAt.Value == default)
        {
            failure = "it does not say when its fiscal day was opened.";
            return null;
        }

        if (sale.ReceiptCounter is not > 0 || sale.ReceiptGlobalNo is not > 0)
        {
            failure = "it carries no receipt sequence, so it has no position in the device's chain.";
            return null;
        }

        if (sale.Lines.Count == 0)
        {
            failure = "it has no lines, and the platform rebuilds the signed receipt from them.";
            return null;
        }

        failure = null;

        return new IngestSignedReceiptApiRequest
        {
            DeviceId = deviceId,
            InvoiceNo = sale.ExternalReferenceId,

            // A van sells; it never issues a credit note out of coverage. The handset signs
            // "FISCALINVOICE" into the payload, so anything else here would fail verification.
            ReceiptType = ReceiptType.FiscalInvoice,
            Currency = sale.Currency,
            ReceiptDate = sale.ReceiptDate,

            // The handset's composer builds tax-inclusive lines, and the platform's tax extraction has to
            // match it or the derived tax block — which is signed — comes out different.
            TaxInclusive = true,
            PaymentType = ResolveMoneyType(sale.PaymentMethod),
            PaymentAmount = sale.TotalAmount,

            // The printed document was a till receipt, not an A4 invoice; the archive should describe what
            // the customer is actually holding.
            ReceiptPrintForm = ReceiptPrintForm.Receipt48,

            // No buyer: the handset signed a receipt without one, and adding buyer details here would
            // archive a document that differs from the one that was printed.
            Buyer = null,

            Lines = sale.Lines
                .OrderBy(line => line.LineNum)
                .Select(line => new LineApiRequest
                {
                    Name = line.ItemDescription,
                    HsCode = line.HsCode,
                    Quantity = line.Quantity,

                    // The tax-inclusive unit price the receipt was signed over. The platform recomputes
                    // the line total and the whole tax block from it.
                    Price = line.UnitPrice,
                    TaxPercent = line.TaxPercent,
                    TaxId = line.TaxId ?? 0,
                    TaxCode = line.TaxCode
                })
                .ToList(),

            FiscalDayNo = fiscalDayNo,
            FiscalDayOpenedAt = sale.FiscalDayOpenedAt.Value,
            ReceiptCounter = sale.ReceiptCounter.Value,
            ReceiptGlobalNo = sale.ReceiptGlobalNo.Value,
            PreviousReceiptHash = sale.PreviousReceiptHash,
            DeviceSignatureHash = sale.DeviceSignatureHash,
            DeviceSignatureValue = sale.DeviceSignatureValue
        };
    }

    /// <summary>
    /// The handset stores the money type by name, from the same enum the platform publishes, so the name
    /// round-trips. An unrecognised value falls back to cash rather than failing the receipt: the money
    /// type does not enter the signature, and refusing a valid receipt over it would be a worse trade
    /// than a mis-stated money-type counter.
    /// </summary>
    /// <remarks>
    /// This used to parse the payment method as a <see cref="MoneyType"/> name, which only ever
    /// matched the sales that paid in cash: a handset records the brand the customer paid with
    /// ("Ecocash", "Innbucks", "Swipe"), and none of those are enum names, so every one of them
    /// fell through to <see cref="MoneyType.Cash"/> and was declared to ZIMRA as cash.
    /// <see cref="TenderTypes"/> maps the brands the tills actually send.
    /// </remarks>
    private static MoneyType ResolveMoneyType(string? paymentMethod) =>
        TenderTypes.ToMoneyType(paymentMethod) ?? MoneyType.Cash;

    private static void MarkUnsignable(DesktopSaleEntity sale, string reason)
    {
        sale.ReceiptIngestStatus = DesktopSaleReceiptIngestStatus.Unsignable;
        sale.ReceiptIngestError = Truncate($"Cannot be submitted: {reason}", 2000);
    }

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>
/// What one drain did. <see cref="DevicesStopped"/> is worth reporting on its own: it counts handsets
/// whose remaining receipts are waiting behind a failure, which is the number that decides whether
/// tonight's fiscal day can be closed.
/// </summary>
public sealed class VanSalesReceiptIngestRunResult
{
    public int Ingested { get; set; }

    public int Failed { get; set; }

    public int ChainBroken { get; set; }

    public int Unsignable { get; set; }

    public int DevicesStopped { get; set; }

    /// <summary>
    /// The platform does not serve the ingest route, so the run stopped without spending any receipt's
    /// attempts. This is a deployment problem — the platform is older than this service — and it is
    /// reported on its own because nothing on the receipts themselves shows it.
    /// </summary>
    public bool PlatformEndpointMissing { get; set; }

    public List<string> Errors { get; } = [];

    public int Total => Ingested + Failed + ChainBroken + Unsignable;
}
