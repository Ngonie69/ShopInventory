using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Posts one incoming payment per business partner per day, settling every till, vending and older
/// desktop app invoice posted before the cut-off. No invoice gets a payment of its own.
/// </summary>
/// <remarks>
/// <para>
/// A pass works on the most recent cut-off that has passed. At 17:00 or later that is today's. Before
/// 17:00 it is yesterday's, so a pass the next morning finishes what an outage stopped the evening
/// before. Invoices posted after the cut-off belong to the next day.
/// </para>
/// <para>
/// A pass does four things, in this order:
/// <list type="number">
/// <item>Asks SAP about payments whose post was sent and never answered.</item>
/// <item>Hands back the invoices of an earlier day's payment that never reached SAP, so today's picks them up.</item>
/// <item>Creates the day's payment for each customer that has none yet, claiming its invoices.</item>
/// <item>Posts the day's payments that have not posted.</item>
/// </list>
/// </para>
/// <para>
/// SAP has no idempotency for a payment, so the ordering is the guarantee. Invoices are claimed before
/// anything is sent, and the post marker is committed before the request goes out. SAP is asked for the
/// payment's reference before every send. An invoice is handed back only once SAP has shown the payment
/// does not exist.
/// </para>
/// </remarks>
public sealed class DailyIncomingPaymentService(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    SapCircuitBreakerState circuitState,
    IOptions<DesktopSalePostingSettings> settings,
    IOptions<SAPSettings> sapSettings,
    ILogger<DailyIncomingPaymentService> logger)
{
    private const int MaxErrorLength = 2000;

    /// <summary>The claims that hold an invoice. A released or empty payment holds nothing.</summary>
    private static readonly DailyIncomingPaymentStatus[] ClaimingStatuses =
    [
        DailyIncomingPaymentStatus.Pending,
        DailyIncomingPaymentStatus.Unresolved,
        DailyIncomingPaymentStatus.Posted
    ];

    public async Task<DailyIncomingPaymentRunResult> RunAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        var result = new DailyIncomingPaymentRunResult();

        if (circuitState.ShouldShortCircuit(out var retryAfter))
        {
            logger.LogInformation(
                "Skipping the daily incoming payment pass: the SAP circuit is open for another {RetryAfter}.",
                retryAfter);
            return result;
        }

        var (paymentDate, cutoffUtc) = PeriodFor(
            utcNow, ParseTime(settings.Value.DailyPaymentTimeCAT), QuartzConfiguration.CatTimeZone);
        result.PaymentDate = paymentDate;

        await ResolveUnresolvedAsync(utcNow, result, cancellationToken);
        await ReleaseEarlierDaysAsync(paymentDate, result);
        await CreatePaymentsAsync(paymentDate, cutoffUtc, result, cancellationToken);
        await PostDuePaymentsAsync(paymentDate, utcNow, result, cancellationToken);

        if (result.HasWork)
        {
            logger.LogInformation(
                "Daily incoming payments for {PaymentDate:yyyy-MM-dd}: {Created} created, {Posted} posted, "
                + "{Adopted} already in SAP, {NothingToPay} with nothing left to pay, {Unresolved} unresolved, "
                + "{Released} released, {Failed} failed.",
                paymentDate, result.Created, result.Posted, result.Adopted, result.NothingToPay,
                result.Unresolved, result.Released, result.Failed);
        }

        return result;
    }

    /// <summary>
    /// The trading day a pass at <paramref name="utcNow"/> settles, and its cut-off as an instant.
    /// </summary>
    internal static (DateTime PaymentDate, DateTime CutoffUtc) PeriodFor(
        DateTime utcNow,
        TimeSpan paymentTime,
        TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        var cutoffLocal = local.Date + paymentTime;

        if (local < cutoffLocal)
        {
            cutoffLocal = cutoffLocal.AddDays(-1);
        }

        var cutoffUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(cutoffLocal, DateTimeKind.Unspecified), zone);

        return (DateTime.SpecifyKind(cutoffLocal.Date, DateTimeKind.Unspecified), cutoffUtc);
    }

    private static TimeSpan ParseTime(string? value) =>
        TimeSpan.TryParse(value, out var time) ? time : new TimeSpan(17, 0, 0);

    // ---- 1. Sent, never answered ------------------------------------------------------------------

    private async Task ResolveUnresolvedAsync(
        DateTime utcNow,
        DailyIncomingPaymentRunResult result,
        CancellationToken cancellationToken)
    {
        var unresolved = await context.DailyIncomingPayments
            .Include(payment => payment.Lines)
            .Where(payment => payment.Status == DailyIncomingPaymentStatus.Unresolved)
            .OrderBy(payment => payment.Id)
            .ToListAsync(cancellationToken);

        var grace = TimeSpan.FromMinutes(Math.Max(0, settings.Value.UnresolvedPostGraceMinutes));

        foreach (var payment in unresolved)
        {
            IncomingPayment? found;
            try
            {
                found = await FindInSapAsync(payment, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                payment.LastError = Truncate(ex.Message);
                result.Unresolved++;
                result.Errors.Add($"{payment.Reference}: {ex.Message}");
                await context.SaveChangesAsync(CancellationToken.None);
                continue;
            }

            if (found is not null)
            {
                await RecordPostedAsync(payment, found.DocEntry, found.DocNum, utcNow);
                result.Adopted++;

                logger.LogWarning(
                    "Daily payment {Reference} was sent without a reply, and SAP holds it as payment {DocNum}. Adopted it.",
                    payment.Reference, found.DocNum);
                continue;
            }

            if (payment.PostIssuedAtUtc is { } issuedAt && utcNow - issuedAt < grace)
            {
                // A payment SAP has committed can take a few minutes to show in a list, so "not there"
                // cannot be trusted yet.
                result.Unresolved++;
                continue;
            }

            payment.LastError = Truncate(
                $"A post was sent at {payment.PostIssuedAtUtc:yyyy-MM-dd HH:mm:ss}Z with no reply, and SAP still shows "
                + "no payment for it. It will be sent again.");
            payment.Status = DailyIncomingPaymentStatus.Pending;
            payment.PostIssuedAtUtc = null;
            await context.SaveChangesAsync(CancellationToken.None);

            logger.LogWarning(
                "Daily payment {Reference} was sent without a reply and SAP does not hold it after {Grace}. It will be sent again.",
                payment.Reference, grace);
        }
    }

    // ---- 2. Earlier days that never posted ----------------------------------------------------------

    private async Task ReleaseEarlierDaysAsync(DateTime paymentDate, DailyIncomingPaymentRunResult result)
    {
        // Pending means nothing was sent, or SAP refused what was. Either way SAP holds no payment, so
        // handing its invoices back cannot put one invoice on two payments.
        var stale = await context.DailyIncomingPayments
            .Where(payment => payment.Status == DailyIncomingPaymentStatus.Pending
                              && payment.PostIssuedAtUtc == null
                              && payment.PaymentDate < paymentDate)
            .ToListAsync();

        foreach (var payment in stale)
        {
            payment.Status = DailyIncomingPaymentStatus.Released;
            payment.LastError = Truncate(
                $"Did not post on {payment.PaymentDate:yyyy-MM-dd}. Its invoices were handed to the "
                + $"{paymentDate:yyyy-MM-dd} payment. Last error: {payment.LastError ?? "none"}");
            result.Released++;

            logger.LogWarning(
                "Daily payment {Reference} never posted; its invoices move to the {PaymentDate:yyyy-MM-dd} payment.",
                payment.Reference, paymentDate);
        }

        if (stale.Count > 0)
        {
            await context.SaveChangesAsync(CancellationToken.None);
        }
    }

    // ---- 3. Claim the day's invoices ------------------------------------------------------------------

    private async Task CreatePaymentsAsync(
        DateTime paymentDate,
        DateTime cutoffUtc,
        DailyIncomingPaymentRunResult result,
        CancellationToken cancellationToken)
    {
        var options = settings.Value;
        var swipe = SwipeSettlementFromSettings();
        var lookbackStart = paymentDate.AddDays(-Math.Max(1, options.DailyPaymentLookbackDays));

        // A customer with any payment for the day is left alone, so a second pass never starts a second
        // payment. Invoices posted since the first wait for tomorrow's.
        var alreadyPaid = await context.DailyIncomingPayments
            .Where(payment => payment.PaymentDate == paymentDate)
            .Select(payment => payment.CardCode)
            .ToListAsync(cancellationToken);

        var sources = SaleSourceSystems.PostedByDesktopSaleJob;

        var sales = await context.DesktopSales
            .Where(sale => sale.SourceSystem != null
                           && sources.Contains(sale.SourceSystem)
                           && sale.ConsolidationStatus == DesktopSaleConsolidationStatus.Consolidated
                           && sale.SapDocEntry != null
                           && sale.PostedAt != null
                           && sale.PostedAt < cutoffUtc
                           && sale.DocDate >= lookbackStart
                           // Failed is a payment the old per-sale route never managed to send. Unmapped
                           // is looked at again because a card code may have been configured since.
                           && (sale.PaymentStatus == null
                               || sale.PaymentStatus == DesktopSalePaymentStatuses.Failed
                               || sale.PaymentStatus == DesktopSalePaymentStatuses.Unmapped)
                           && !alreadyPaid.Contains(sale.CardCode)
                           && !context.DailyIncomingPaymentLines.Any(line =>
                               line.DesktopSaleId == sale.Id
                               && ClaimingStatuses.Contains(line.DailyIncomingPayment!.Status)))
            .OrderBy(sale => sale.Id)
            .ToListAsync(cancellationToken);

        var consolidations = await context.SaleConsolidations
            .Where(consolidation => consolidation.SapDocEntry != null
                                    && consolidation.PostedAt != null
                                    && consolidation.PostedAt < cutoffUtc
                                    && consolidation.ConsolidationDate >= lookbackStart
                                    && (consolidation.PaymentStatus == null
                                        || consolidation.PaymentStatus == DesktopSalePaymentStatuses.Failed
                                        || consolidation.PaymentStatus == DesktopSalePaymentStatuses.Unmapped)
                                    && !alreadyPaid.Contains(consolidation.CardCode)
                                    && !context.DailyIncomingPaymentLines.Any(line =>
                                        line.SaleConsolidationId == consolidation.Id
                                        && ClaimingStatuses.Contains(line.DailyIncomingPayment!.Status)))
            .OrderBy(consolidation => consolidation.Id)
            .ToListAsync(cancellationToken);

        var consolidationIds = consolidations.Select(consolidation => consolidation.Id).ToList();
        var linkedSales = consolidationIds.Count == 0
            ? []
            : await context.DesktopSales
                .Where(sale => sale.ConsolidationId != null && consolidationIds.Contains(sale.ConsolidationId.Value))
                .ToListAsync(cancellationToken);

        var claims = new List<(string CardCode, string? CardName, DailyIncomingPaymentLineEntity Line)>();

        // In memory: SQLite, which the tests run on, cannot compare decimals.
        foreach (var sale in sales.Where(sale => sale.AmountPaid > 0))
        {
            var split = DailyIncomingPaymentBuilder.SplitSale(sale, swipe);
            if (!split.IsMapped)
            {
                sale.PaymentStatus = DesktopSalePaymentStatuses.Unmapped;
                sale.LastPaymentError = Truncate(split.Reason);
                continue;
            }

            claims.Add((sale.CardCode, sale.CardName, LineFor(split.Split!.Value, sale.SapDocEntry!.Value, sale.SapDocNum, saleId: sale.Id)));
        }

        foreach (var consolidation in consolidations)
        {
            var split = DailyIncomingPaymentBuilder.SplitConsolidation(
                consolidation,
                linkedSales.Where(sale => sale.ConsolidationId == consolidation.Id).ToList(),
                swipe);

            if (!split.IsMapped)
            {
                consolidation.PaymentStatus = DesktopSalePaymentStatuses.Unmapped;
                consolidation.LastError = Truncate($"Daily payment: {split.Reason}");
                continue;
            }

            if (split.Split!.Value.Total <= 0m)
            {
                continue;
            }

            claims.Add((consolidation.CardCode, consolidation.CardName,
                LineFor(split.Split.Value, consolidation.SapDocEntry!.Value, consolidation.SapDocNum, consolidationId: consolidation.Id)));
        }

        await context.SaveChangesAsync(CancellationToken.None);

        foreach (var group in claims.GroupBy(claim => claim.CardCode, StringComparer.OrdinalIgnoreCase))
        {
            var lines = group.Select(claim => claim.Line).ToList();
            var payment = new DailyIncomingPaymentEntity
            {
                CardCode = group.First().CardCode,
                CardName = group.Select(claim => claim.CardName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                PaymentDate = paymentDate,
                Reference = DailyIncomingPaymentBuilder.ReferenceFor(paymentDate, group.First().CardCode),
                Status = DailyIncomingPaymentStatus.Pending,
                Lines = lines
            };
            RecomputeSums(payment);

            context.DailyIncomingPayments.Add(payment);

            try
            {
                // One customer at a time, so a clash on the unique index costs that customer only.
                await context.SaveChangesAsync(CancellationToken.None);
                result.Created++;
            }
            catch (DbUpdateException ex)
            {
                context.Entry(payment).State = EntityState.Detached;
                foreach (var line in lines)
                {
                    context.Entry(line).State = EntityState.Detached;
                }

                result.Failed++;
                result.Errors.Add($"{payment.Reference}: {ex.GetBaseException().Message}");

                logger.LogWarning(
                    ex,
                    "Could not claim {Reference}: another pass may have created it. Left for that pass.",
                    payment.Reference);
            }
        }
    }

    private static DailyIncomingPaymentLineEntity LineFor(
        TenderSplit split,
        int invoiceDocEntry,
        int? invoiceDocNum,
        int? saleId = null,
        int? consolidationId = null) =>
        new()
        {
            DesktopSaleId = saleId,
            SaleConsolidationId = consolidationId,
            InvoiceDocEntry = invoiceDocEntry,
            InvoiceDocNum = invoiceDocNum,
            CashAmount = split.Cash,
            TransferAmount = split.Transfer,
            CreditAmount = split.Credit
        };

    // ---- 4. Post ------------------------------------------------------------------------------------

    private async Task PostDuePaymentsAsync(
        DateTime paymentDate,
        DateTime utcNow,
        DailyIncomingPaymentRunResult result,
        CancellationToken cancellationToken)
    {
        var maxAttempts = settings.Value.MaxPostingAttempts;

        var due = await context.DailyIncomingPayments
            .Include(payment => payment.Lines)
            .Where(payment => payment.Status == DailyIncomingPaymentStatus.Pending
                              && payment.PaymentDate == paymentDate
                              && payment.Attempts < maxAttempts)
            .OrderBy(payment => payment.Id)
            .ToListAsync(cancellationToken);

        foreach (var payment in due)
        {
            if (cancellationToken.IsCancellationRequested || circuitState.ShouldShortCircuit(out _))
            {
                break;
            }

            try
            {
                await PostOneAsync(payment, utcNow, result, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                payment.LastError = Truncate(ex.Message);
                if (!SapFailureClassifier.IsTransient(ex, cancellationToken))
                {
                    payment.Attempts++;
                }

                result.Failed++;
                result.Errors.Add($"{payment.Reference}: {ex.Message}");

                logger.LogError(
                    ex,
                    "Daily payment {Reference} did not post. Attempt {Attempt} of {Max}.",
                    payment.Reference, payment.Attempts, maxAttempts);
            }

            await context.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task PostOneAsync(
        DailyIncomingPaymentEntity payment,
        DateTime utcNow,
        DailyIncomingPaymentRunResult result,
        CancellationToken cancellationToken)
    {
        // Asked before the balances, not after. A payment a lost reply left in SAP has already paid its
        // invoices, so reading the balances first would show nothing to pay and leave that payment
        // recorded nowhere.
        var existing = await FindInSapAsync(payment, cancellationToken);
        if (existing is not null)
        {
            await RecordPostedAsync(payment, existing.DocEntry, existing.DocNum, utcNow);
            result.Adopted++;
            return;
        }

        var invoices = await sapClient.GetInvoiceBalancesByDocEntriesAsync(
            payment.Lines.Select(line => line.InvoiceDocEntry), cancellationToken);

        await FitLinesToBalancesAsync(payment, invoices.ToDictionary(invoice => invoice.DocEntry), utcNow, cancellationToken);
        RecomputeSums(payment);

        if (payment.Lines.Count == 0)
        {
            payment.Status = DailyIncomingPaymentStatus.NothingToPay;
            payment.LastError = null;
            await context.SaveChangesAsync(CancellationToken.None);
            result.NothingToPay++;
            return;
        }

        var currencies = invoices
            .Where(invoice => payment.Lines.Any(line => line.InvoiceDocEntry == invoice.DocEntry))
            .Select(invoice => invoice.DocCurrency)
            .Where(currency => !string.IsNullOrWhiteSpace(currency))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (currencies.Count > 1)
        {
            throw new InvalidOperationException(
                $"Daily payment {payment.Reference} would settle invoices in {string.Join(", ", currencies)}. "
                + "One payment cannot carry two currencies.");
        }

        var swipe = SwipeSettlementFromSettings();

        if (swipe.AsTransfer && payment.CreditSum > 0m && payment.TransferSum > 0m)
        {
            // Both kinds of transferred money on one document, and SAP carries one account for the pair.
            // The account is left off rather than applied to money it does not belong to; this says so
            // out loud, because otherwise it is only findable by reconciling the bank by hand.
            logger.LogWarning(
                "Daily payment {Reference} settles {Card} of card money and {Wallet} of wallet money together. "
                + "SAP takes one transfer account per payment, so {Account} is left off and SAP's default "
                + "applies to both.",
                payment.Reference, payment.CreditSum, payment.TransferSum, swipe.TransferAccount);
        }

        var request = DailyIncomingPaymentBuilder.BuildRequest(payment, swipe);

        // The last point at which walking away costs nothing.
        cancellationToken.ThrowIfCancellationRequested();

        // Durable before the request goes out, and committed on its own, together with the lines as they
        // will be sent. A lost reply is then known about and resolvable from what was actually asked for.
        payment.PostIssuedAtUtc = utcNow;
        await context.SaveChangesAsync(CancellationToken.None);

        try
        {
            var created = await sapClient.CreateIncomingPaymentAsync(request, CancellationToken.None);
            await RecordPostedAsync(payment, created.DocEntry, created.DocNum, utcNow);
            result.Posted++;

            logger.LogInformation(
                "Posted daily payment {Reference} as SAP payment {DocNum}: {Count} invoice(s), cash {Cash}, transfer {Transfer}, card {Card}.",
                payment.Reference, created.DocNum, payment.Lines.Count, payment.CashSum, payment.TransferSum, payment.CreditSum);
        }
        catch (Exception ex) when (SapFailureClassifier.DefinitelyNotCommitted(ex))
        {
            // SAP refused it, so nothing exists and the marker comes off. The caller records the refusal.
            payment.PostIssuedAtUtc = null;
            throw;
        }
        catch (Exception ex)
        {
            payment.Status = DailyIncomingPaymentStatus.Unresolved;
            payment.LastError = Truncate(
                $"Sent to SAP and the outcome is unknown ({ex.Message}). It will not be sent again until SAP has been "
                + $"asked for '{payment.Reference}'.");
            await context.SaveChangesAsync(CancellationToken.None);
            result.Unresolved++;

            logger.LogError(
                ex,
                "Daily payment {Reference} was sent and SAP's answer was lost. It is held until SAP has been asked for it.",
                payment.Reference);
        }
    }

    /// <summary>
    /// Takes out what SAP shows as already settled or gone, and reduces a till line to what its invoice
    /// still owes.
    /// </summary>
    private async Task FitLinesToBalancesAsync(
        DailyIncomingPaymentEntity payment,
        IReadOnlyDictionary<int, Invoice> invoices,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var (sales, consolidations) = await LoadMembersAsync(payment, cancellationToken);

        foreach (var line in payment.Lines.ToList())
        {
            invoices.TryGetValue(line.InvoiceDocEntry, out var invoice);
            var sale = line.DesktopSaleId is { } saleId ? sales.GetValueOrDefault(saleId) : null;
            var consolidation = line.SaleConsolidationId is { } consolidationId
                ? consolidations.GetValueOrDefault(consolidationId)
                : null;

            if (invoice is null || string.Equals(invoice.Cancelled, "tYES", StringComparison.OrdinalIgnoreCase))
            {
                var reason = $"Invoice {line.InvoiceDocNum?.ToString() ?? line.InvoiceDocEntry.ToString()} is not in SAP "
                             + "or is cancelled, so the daily payment left it out.";
                if (sale is not null)
                {
                    sale.LastPaymentError = reason;
                }

                if (consolidation is not null)
                {
                    consolidation.LastError = reason;
                }

                DropLine(payment, line);
                continue;
            }

            line.InvoiceDocNum = invoice.DocNum;
            var open = Math.Round(invoice.DocTotal - invoice.PaidToDate, 2);

            if (open <= 0m)
            {
                // Settled in SAP some other way, usually by hand. Recorded as settled rather than
                // paid again.
                if (sale is not null)
                {
                    sale.PaymentStatus = DesktopSalePaymentStatuses.PostedUnconfirmed;
                    sale.PaymentPostedAt = utcNow;
                    sale.LastPaymentError = null;
                }

                if (consolidation is not null)
                {
                    consolidation.PaymentStatus = DesktopSalePaymentStatuses.PostedUnconfirmed;
                    consolidation.PaymentPostedAt = utcNow;
                }

                DropLine(payment, line);
                continue;
            }

            if (open >= line.SumApplied)
            {
                continue;
            }

            if (consolidation is not null)
            {
                // Several tenders and no way to know which one the settlement already made covered. A
                // person decides.
                consolidation.PaymentStatus = DesktopSalePaymentStatuses.NeedsReview;
                consolidation.LastError = Truncate(
                    $"Invoice {invoice.DocNum} owes {open:N2} but its sales paid {line.SumApplied:N2}. It is partly "
                    + "settled in SAP already, so the daily payment left it for a person to settle.");
                DropLine(payment, line);
                continue;
            }

            var capped = DailyIncomingPaymentBuilder.CapTo(
                new TenderSplit(line.CashAmount, line.TransferAmount, line.CreditAmount), open);
            line.CashAmount = capped.Cash;
            line.TransferAmount = capped.Transfer;
            line.CreditAmount = capped.Credit;
        }
    }

    private void DropLine(DailyIncomingPaymentEntity payment, DailyIncomingPaymentLineEntity line)
    {
        payment.Lines.Remove(line);
        context.DailyIncomingPaymentLines.Remove(line);
    }

    private async Task RecordPostedAsync(
        DailyIncomingPaymentEntity payment,
        int docEntry,
        int docNum,
        DateTime utcNow)
    {
        payment.Status = DailyIncomingPaymentStatus.Posted;
        payment.SapDocEntry = docEntry;
        payment.SapDocNum = docNum;
        payment.PostedAtUtc = utcNow;
        payment.LastError = null;

        var (sales, consolidations) = await LoadMembersAsync(payment, CancellationToken.None);

        foreach (var sale in sales.Values)
        {
            sale.PaymentSapDocEntry = docEntry;
            sale.PaymentSapDocNum = docNum;
            sale.PaymentPostedAt = utcNow;
            sale.PaymentStatus = DesktopSalePaymentStatuses.Posted;
            sale.LastPaymentError = null;
        }

        foreach (var consolidation in consolidations.Values)
        {
            consolidation.PaymentSapDocEntry = docEntry;
            consolidation.PaymentSapDocNum = docNum;
            consolidation.PaymentPostedAt = utcNow;
            consolidation.PaymentStatus = DesktopSalePaymentStatuses.Posted;
        }

        await context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<(Dictionary<int, DesktopSaleEntity> Sales, Dictionary<int, SaleConsolidationEntity> Consolidations)>
        LoadMembersAsync(DailyIncomingPaymentEntity payment, CancellationToken cancellationToken)
    {
        var saleIds = payment.Lines.Where(line => line.DesktopSaleId != null).Select(line => line.DesktopSaleId!.Value).ToList();
        var consolidationIds = payment.Lines.Where(line => line.SaleConsolidationId != null).Select(line => line.SaleConsolidationId!.Value).ToList();

        var sales = saleIds.Count == 0
            ? new Dictionary<int, DesktopSaleEntity>()
            : await context.DesktopSales.Where(sale => saleIds.Contains(sale.Id)).ToDictionaryAsync(sale => sale.Id, cancellationToken);

        var consolidations = consolidationIds.Count == 0
            ? new Dictionary<int, SaleConsolidationEntity>()
            : await context.SaleConsolidations.Where(c => consolidationIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);

        return (sales, consolidations);
    }

    /// <summary>
    /// The customer's payment for the day that carries this reference, if SAP holds one.
    /// </summary>
    /// <remarks>
    /// Throws when SAP cannot be asked. Treating "could not ask" as "not there" is how a day gets paid twice.
    /// </remarks>
    private async Task<IncomingPayment?> FindInSapAsync(
        DailyIncomingPaymentEntity payment,
        CancellationToken cancellationToken)
    {
        List<IncomingPayment> payments;
        try
        {
            payments = await sapClient.GetIncomingPaymentsByCustomerAsync(
                payment.CardCode, payment.PaymentDate, payment.PaymentDate, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Not sending daily payment {payment.Reference}: SAP could not be asked whether it already holds it. {ex.Message}",
                ex);
        }

        // The colon closes the reference, so DAYPAY-20260914-C1 cannot match DAYPAY-20260914-C10.
        var prefix = payment.Reference + ":";

        return payments
            .Where(candidate => candidate.Remarks is not null
                                && candidate.Remarks.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(candidate.Cancelled, "tYES", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.DocEntry)
            .FirstOrDefault();
    }

    /// <summary>How a swipe reaches SAP, as configured. See <see cref="SwipeSettlement"/>.</summary>
    private SwipeSettlement SwipeSettlementFromSettings() =>
        new(sapSettings.Value.SwipeCreditCardCode, sapSettings.Value.SwipeTransferAccount);

    private static void RecomputeSums(DailyIncomingPaymentEntity payment)
    {
        payment.CashSum = payment.Lines.Sum(line => line.CashAmount);
        payment.TransferSum = payment.Lines.Sum(line => line.TransferAmount);
        payment.CreditSum = payment.Lines.Sum(line => line.CreditAmount);
    }

    private static string? Truncate(string? value) =>
        string.IsNullOrEmpty(value) || value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}

/// <summary>What one daily payment pass did.</summary>
public sealed class DailyIncomingPaymentRunResult
{
    public DateTime PaymentDate { get; set; }
    public int Created { get; set; }
    public int Posted { get; set; }
    public int Adopted { get; set; }
    public int NothingToPay { get; set; }
    public int Unresolved { get; set; }
    public int Released { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = [];

    public bool HasWork =>
        Created + Posted + Adopted + NothingToPay + Unresolved + Released + Failed > 0;
}
