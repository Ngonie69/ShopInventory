using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Mobile;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.Notifications;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;

public sealed class CreateDesktopSaleHandler(
    ApplicationDbContext context,
    DesktopSaleFiscaliser fiscaliser,
    IInventoryLockService lockService,
    IStockLedger stockLedger,
    IHubContext<NotificationHub> hubContext,
    IIdempotencyRequestStore idempotencyRequestStore,
    IOptions<TaxSettings> taxSettings,
    IAuditService auditService,
    ILogger<CreateDesktopSaleHandler> logger
) : IRequestHandler<CreateDesktopSaleCommand, ErrorOr<CreateDesktopSaleResult>>
{
    private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sells, then records that it sold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row is written for every outcome, around the whole attempt rather than at the point of
    /// sale, so a refusal leaves a trace too. A till that cannot sell is worth as much to whoever is
    /// reading the trail as one that can — more, usually, because the successful sale is also a
    /// document and the refusal is nothing at all.
    /// </para>
    /// <para>
    /// A replay is recorded as a replay. The idempotent path answers an existing sale rather than
    /// making a second one, and a trail that showed two creations for one sale would invent a
    /// duplicate that never happened.
    /// </para>
    /// </remarks>
    public async Task<ErrorOr<CreateDesktopSaleResult>> Handle(
        CreateDesktopSaleCommand command,
        CancellationToken cancellationToken)
    {
        var outcome = await SellAsync(command, cancellationToken);

        await auditService.LogAsync(
            AuditActions.CreateDesktopSale,
            nameof(DesktopSaleEntity),
            outcome.IsError ? command.Request.ExternalReferenceId : outcome.Value.Sale.ExternalReferenceId,
            Describe(command, outcome),
            !outcome.IsError,
            outcome.IsError ? outcome.FirstError.Description : null);

        return outcome;
    }

    /// <summary>The row's text. Internal so it can be asserted without standing up a till.</summary>
    internal static string Describe(
        CreateDesktopSaleCommand command,
        ErrorOr<CreateDesktopSaleResult> outcome)
    {
        if (outcome.IsError)
        {
            var reference = string.IsNullOrWhiteSpace(command.Request.ExternalReferenceId)
                ? "an unreferenced sale"
                : $"sale {command.Request.ExternalReferenceId}";

            return $"Refused {reference} over {command.Request.Lines.Count} line(s), "
                + $"tendered {command.Request.PaymentMethod}.";
        }

        var sale = outcome.Value.Sale;
        var verb = outcome.Value.WasExisting ? "Replayed" : "Sold";
        var fiscal = string.IsNullOrWhiteSpace(sale.FiscalReceiptNumber)
            ? sale.FiscalizationStatus
            : $"{sale.FiscalizationStatus}, receipt {sale.FiscalReceiptNumber}";

        return $"{verb} {sale.ExternalReferenceId} to {sale.CardCode} from {sale.WarehouseCode}: "
            + $"{sale.TotalAmount:0.00} incl. {sale.VatAmount:0.00} VAT, "
            + $"tendered {command.Request.PaymentMethod}. Fiscalisation {fiscal}.";
    }

    private async Task<ErrorOr<CreateDesktopSaleResult>> SellAsync(
        CreateDesktopSaleCommand command,
        CancellationToken cancellationToken)
    {
        var req = command.Request;

        // A till sells as the account that signed in. Who the sale invoices, which warehouse the
        // stock leaves and which cost centre it books to are read from there — the request used to
        // say all three and nothing checked them, so any authenticated till could sell from any
        // warehouse as any customer. Resolved before the idempotency acquire so an account that
        // cannot sell is turned away without leaving a request record behind.
        // The shop is included because a till operator's three values live on it rather than on the
        // account, and SellingAccountResolver refuses to fall back to the account's own columns when
        // a shop is named but absent — selling on the values the shop was meant to replace is exactly
        // the confusion this whole path exists to remove.
        var user = await context.Users
            .AsNoTracking()
            .Include(u => u.Shop)
            .FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);

        var assignments = SellingAccountResolver.Resolve(user);
        if (assignments.IsError)
        {
            return assignments.Errors;
        }

        var account = assignments.Value;

        var mismatch = ApplyAccountToRequest(req, account);
        if (mismatch is not null)
        {
            return mismatch.Value;
        }

        // Vending bills a named vendor rather than whoever walks in, so the vendor is resolved here —
        // against the ones assigned to this account's business partner and still active. Resolving it
        // server-side is what makes deactivating a vendor stop it trading: a till holding a stale list,
        // or a caller naming a code directly, is refused rather than obeyed.
        var vendorResult = await ResolveVendorAsync(req, account, cancellationToken);
        if (vendorResult.IsError)
        {
            return vendorResult.Errors;
        }

        var vendor = vendorResult.Value;

        var normalizedExternalReference = string.IsNullOrWhiteSpace(req.ExternalReferenceId)
            ? null
            : req.ExternalReferenceId.Trim();
        req.ExternalReferenceId = normalizedExternalReference;

        var externalRef = normalizedExternalReference ??
            $"DS-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8]}";

        // Taken here, at the same point and off the same object the idempotency store hashes, so the
        // two guards on this key compare the same bytes. It is carried onto the sale row, which is
        // the guard that outlives the store's record.
        var requestHash = IdempotencyRequestHash.Of(req);

        long? idempotencyRequestId = null;
        var releaseIdempotencyRequest = false;

        try
        {
            if (!string.IsNullOrWhiteSpace(normalizedExternalReference))
            {
                var acquireResult = await idempotencyRequestStore.TryAcquireAsync<DesktopSaleResponseDto>(
                    "desktop-sales.create",
                    normalizedExternalReference,
                    req,
                    cancellationToken);

                switch (acquireResult.Outcome)
                {
                    case IdempotencyAcquireOutcome.ReplayAvailable when acquireResult.Response is not null:
                        return new CreateDesktopSaleResult(acquireResult.Response, WasExisting: true);
                    case IdempotencyAcquireOutcome.InProgress:
                        return Errors.Idempotency.RequestInProgress("desktop sale creation");
                    case IdempotencyAcquireOutcome.RequestMismatch:
                        return Errors.Idempotency.RequestMismatch("desktop sale creation");
                    case IdempotencyAcquireOutcome.Acquired:
                        idempotencyRequestId = acquireResult.RequestId;
                        releaseIdempotencyRequest = true;
                        break;
                }
            }

            var existing = await context.DesktopSales
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.ExternalReferenceId == externalRef, cancellationToken);

            if (existing != null)
            {
                switch (VerifyReplay(existing, requestHash))
                {
                    case ReplayVerdict.Refuse:
                        logger.LogError(
                            "Desktop sale {Reference} already exists for a different request (sale "
                            + "{SaleId} created {CreatedAt:u}); refusing rather than replaying it",
                            externalRef,
                            existing.Id,
                            existing.CreatedAt);

                        return Errors.Idempotency.RequestMismatch("desktop sale creation");

                    case ReplayVerdict.Unverifiable:
                        logger.LogWarning(
                            "Desktop sale {Reference} predates request fingerprinting, so this replay "
                            + "was answered without comparing the payload",
                            externalRef);
                        break;
                }

                var existingResponse = MapToResponse(existing);

                if (idempotencyRequestId.HasValue)
                {
                    try
                    {
                        await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, existingResponse, cancellationToken);
                        releaseIdempotencyRequest = false;
                    }
                    catch (Exception completeException)
                    {
                        logger.LogWarning(completeException, "Failed to persist desktop sale idempotency replay for request {RequestId}", idempotencyRequestId.Value);
                    }
                }

                return new CreateDesktopSaleResult(existingResponse, WasExisting: true);
            }

            // The document's accounting date, not a snapshot lookup — the stock ledger resolves its
            // own day, and does not resolve it this way. Left on UtcNow.Date deliberately: this
            // value reaches SAP as the invoice DocDate and the fiscal receipt's date, so moving it
            // is a fiscal change rather than a stock one and does not belong in this pass.
            var today = DateTime.UtcNow.Date;
            var docDate = !string.IsNullOrEmpty(req.DocDate)
                ? DateTime.Parse(req.DocDate).Date
                : today;

            // Acquire per-item/warehouse locks to serialize concurrent sales affecting the same stock
            var lockRequests = req.Lines
                .Select(l => new InventoryLockRequest
                {
                    ItemCode = l.ItemCode,
                    WarehouseCode = l.WarehouseCode
                })
                .DistinctBy(l => $"{l.ItemCode}:{l.WarehouseCode}")
                .ToList();

            var lockResult = await lockService.TryAcquireMultipleLocksAsync(
                lockRequests, LockDuration, cancellationToken);

            if (!lockResult.AllAcquired)
            {
                var failedItems = string.Join(", ",
                    lockResult.FailedLocks.Select(f => $"{f.ItemCode}@{f.WarehouseCode}"));
                logger.LogWarning("Could not acquire stock locks for items: {Items}", failedItems);
                return Error.Conflict(
                    "DesktopSales.StockLocked",
                    $"Stock is currently being modified by another sale. Retry shortly. Affected: {failedItems}");
            }

            try
            {
                // Validate + deduct inside the lock with retry on concurrency conflict
                var result = await ValidateDeductAndCreateSaleAsync(
                    req, externalRef, requestHash, docDate, account, vendor, cancellationToken);

                if (!result.IsError && idempotencyRequestId.HasValue)
                {
                    try
                    {
                        await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, result.Value, cancellationToken);
                        releaseIdempotencyRequest = false;
                    }
                    catch (Exception completeException)
                    {
                        logger.LogWarning(completeException, "Failed to persist desktop sale idempotency completion for request {RequestId}", idempotencyRequestId.Value);
                    }
                }

                return result.IsError
                    ? result.Errors
                    : new CreateDesktopSaleResult(result.Value, WasExisting: false);
            }
            finally
            {
                // Always release locks
                await lockService.ReleaseMultipleLocksAsync(lockResult.LockTokens);
            }
        }
        finally
        {
            if (releaseIdempotencyRequest && idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None);
                }
                catch (Exception releaseException)
                {
                    logger.LogWarning(releaseException, "Failed to release desktop sale idempotency request {RequestId}", idempotencyRequestId.Value);
                }
            }
        }
    }

    /// <summary>
    /// Points the request at the account's own customer and warehouse, refusing it if it asked for
    /// different ones. Returns null when the request is now consistent with the account.
    /// </summary>
    /// <remarks>
    /// Rewriting the LINE warehouses is the part that matters. Lock acquisition, stock validation and
    /// the snapshot deduction all key on <see cref="CreateDesktopSaleLineRequest.WarehouseCode"/>, so
    /// deriving only the header would leave what actually gets deducted in the caller's hands.
    ///
    /// A conflict is refused rather than quietly corrected: a till that believes it sold from one
    /// warehouse while the server sold from another is exactly the confusion this exists to remove.
    /// Sending nothing is the normal case and always succeeds.
    /// </remarks>
    /// <summary>
    /// Resolves the vendor a vending sale is billed to, or null when the sale is not a vending one.
    /// </summary>
    /// <remarks>
    /// Scoped to the caller's own business partner and to active vendors only, by reusing the same
    /// query the vendor list is drawn from — so the list an operator sees and the set the server will
    /// accept cannot drift apart.
    /// </remarks>
    private async Task<ErrorOr<RouteCustomerEntity?>> ResolveVendorAsync(
        CreateDesktopSaleRequest req,
        SellingAccountAssignments account,
        CancellationToken cancellationToken)
    {
        var isVending = SaleSourceSystems.FiscalisesInBackground(
            SaleSourceSystems.NormalizeTillSource(req.SourceSystem));

        var vendorCode = string.IsNullOrWhiteSpace(req.VendorCode) ? null : req.VendorCode.Trim();

        if (!isVending)
        {
            // A shop till bills the walk-in customer its account stands for. A vendor code here means
            // the caller thinks it is doing something this endpoint will not do, so say so rather than
            // dropping it.
            return vendorCode is null
                ? (RouteCustomerEntity?)null
                : Errors.DesktopSales.VendorNotAvailable(vendorCode);
        }

        if (vendorCode is null)
        {
            return Errors.DesktopSales.VendorRequired;
        }

        var vendor = await VanSalesRouteCustomerScope.FindAssignableAsync(
            context, account.CardCode, vendorCode, cancellationToken);

        return vendor is null
            ? Errors.DesktopSales.VendorNotAvailable(vendorCode)
            : vendor;
    }

    /// <remarks>
    /// Internal rather than private so the line-warehouse rewrite can be asserted directly. It is the
    /// step that decides which stock is deducted, and it is not worth reaching through a fully mocked
    /// handler to check it.
    /// </remarks>
    /// <summary>What to do with a request whose reference already has a sale.</summary>
    internal enum ReplayVerdict
    {
        /// <summary>The same request as the one that made the sale. Answer with it.</summary>
        Replay,

        /// <summary>
        /// The sale predates request fingerprinting, so the two cannot be compared.
        /// </summary>
        Unverifiable,

        /// <summary>A different request under a reference that is already spent.</summary>
        Refuse,
    }

    /// <summary>
    /// Whether an existing sale under this reference answers this request.
    /// </summary>
    /// <remarks>
    /// The permanent half of the duplicate guard, and until 10 September 2026 it did not exist: the
    /// lookup returned whatever row carried the reference, compared against nothing. The other half —
    /// <see cref="IdempotencyRequestStore"/> — does make this comparison and refuses a mismatch, but
    /// its record expires after an hour while the sale row does not, so past the hour a re-used
    /// reference was answered with an unrelated invoice under a 201. A till took that for its own
    /// sale, printed it, banked $7.22 against a $7.00 document and deducted stock the document never
    /// held.
    ///
    /// <para>
    /// <see cref="ReplayVerdict.Unverifiable"/> is the honest answer for a row written before the
    /// fingerprint column existed, and it replays rather than refuses. Refusing would turn every
    /// legitimate retry of a pre-migration sale into a supervisor call, to close a hole that closes
    /// itself as those rows age out — and the till now checks the sale's own age, so a stale answer
    /// no longer passes unnoticed there either.
    /// </para>
    /// </remarks>
    internal static ReplayVerdict VerifyReplay(DesktopSaleEntity existing, string requestHash) =>
        string.IsNullOrWhiteSpace(existing.RequestHash)
            ? ReplayVerdict.Unverifiable
            : string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                ? ReplayVerdict.Replay
                : ReplayVerdict.Refuse;

    internal static Error? ApplyAccountToRequest(
        CreateDesktopSaleRequest req,
        SellingAccountAssignments account)
    {
        if (!string.IsNullOrWhiteSpace(req.CardCode) &&
            !string.Equals(req.CardCode.Trim(), account.CardCode, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.DesktopSales.AssignmentMismatch("customer", req.CardCode.Trim(), account.CardCode);
        }

        if (!string.IsNullOrWhiteSpace(req.WarehouseCode) &&
            !string.Equals(req.WarehouseCode.Trim(), account.WarehouseCode, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.DesktopSales.AssignmentMismatch("warehouse", req.WarehouseCode.Trim(), account.WarehouseCode);
        }

        foreach (var line in req.Lines)
        {
            if (!string.IsNullOrWhiteSpace(line.WarehouseCode) &&
                !string.Equals(line.WarehouseCode.Trim(), account.WarehouseCode, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.DesktopSales.AssignmentMismatch("warehouse", line.WarehouseCode.Trim(), account.WarehouseCode);
            }
        }

        req.CardCode = account.CardCode;
        req.WarehouseCode = account.WarehouseCode;

        foreach (var line in req.Lines)
        {
            line.WarehouseCode = account.WarehouseCode;
        }

        return null;
    }

    /// <summary>
    /// The VAT group each of these items is sold under, from the local copy of the item master.
    /// </summary>
    /// <remarks>
    /// The local copy and never SAP directly. Reading the item master costs a paged sweep of every
    /// valid item against a concurrency limit shared with everything else the process does, and a
    /// customer at a counter is the worst person to charge for it — <see cref="SapItemTaxGroupWarmJob"/>
    /// pays it nightly instead.
    ///
    /// <para>
    /// Empty on any failure, and empty is safe: a line with no answer keeps whatever the request
    /// said, which is what every line had before this existed. A sale is never refused over a tax
    /// lookup — the customer is at the counter and the basket is already rung up.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<string, string>> ResolveVatGroupsAsync(
        IEnumerable<string?> itemCodes,
        CancellationToken ct)
    {
        var codes = itemCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codes.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var rows = await context.SapItemTaxGroups
                .AsNoTracking()
                .Where(row => codes.Contains(row.ItemCode))
                .ToListAsync(ct);

            var resolved = rows
                .GroupBy(row => row.ItemCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().VatGroup, StringComparer.OrdinalIgnoreCase);

            // Named, not counted. An item with no VAT group is sold at the standard rate, so this is
            // the line that says which customer was overcharged and on what - and the first sign
            // that the nightly warm has not run or that an item is newer than its last pass.
            var missing = codes.Where(code => !resolved.ContainsKey(code)).ToList();
            if (missing.Count > 0)
            {
                logger.LogWarning(
                    "No VAT group stored for {Count} item(s) on this sale: {Items}. They are taxed at "
                    + "the standard rate, which is wrong for anything zero-rated or exempt.",
                    missing.Count, string.Join(", ", missing));
            }

            return resolved;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Could not read item VAT groups; this sale is taxed at the standard rate throughout.");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The tax code one line is charged and declared under.
    /// </summary>
    /// <remarks>
    /// The item master first. What the request asked for stands in only where the master has no
    /// answer, which keeps a sale behaving exactly as it did before this lookup existed rather than
    /// changing what an unresolved line is charged.
    /// </remarks>
    internal static string? TaxCodeFor(
        CreateDesktopSaleLineRequest line,
        IReadOnlyDictionary<string, string> vatGroups)
    {
        var itemCode = line.ItemCode?.Trim();

        return !string.IsNullOrEmpty(itemCode) && vatGroups.TryGetValue(itemCode, out var vatGroup)
            ? vatGroup
            : line.TaxCode;
    }

    private async Task<ErrorOr<DesktopSaleResponseDto>> ValidateDeductAndCreateSaleAsync(
        CreateDesktopSaleRequest req,
        string externalRef,
        string requestHash,
        DateTime docDate,
        SellingAccountAssignments account,
        RouteCustomerEntity? vendor,
        CancellationToken ct)
    {
        // Check and take in one step, against the ledger the web invoice path now shares. This used
        // to be a local validate-then-deduct against the snapshot that only till sales ever wrote
        // to, which is why the same units could be sold here and on the web within the same minute.
        var claim = req.Lines
            .Select(line => new StockLedgerLine(line.ItemCode, line.WarehouseCode, line.Quantity))
            .ToList();

        var ledgerOutcome = await stockLedger.TryCommitAsync(claim, externalRef, ct);

        // A till sells only from warehouses the morning job covers, so no snapshot means the figures
        // it would sell against do not exist. Refusing is the old behaviour and the right one — an
        // absent snapshot read as zero gave the same answer, by accident.
        if (ledgerOutcome.UntrackedWarehouses.Count > 0)
        {
            return Errors.DesktopSales.InsufficientStock(
                req.Lines[0].ItemCode,
                ledgerOutcome.UntrackedWarehouses[0],
                req.Lines[0].Quantity,
                0);
        }

        if (!ledgerOutcome.Committed)
        {
            return Errors.DesktopSales.StockLedgerRefused(string.Join("; ", ledgerOutcome.Shortfalls));
        }

        var tax = taxSettings.Value;

        // What each item is actually taxed at, from the item master's own VAT group.
        //
        // A till sends no tax code and should not: it has no source for one, and a tax code arriving
        // from a client is a tax code a client can get wrong. Without this every line fell to the
        // standard rate, so a zero-rated item was charged 15.5% the customer did not owe and the
        // receipt declared to ZIMRA said the same thing.
        var vatGroups = await ResolveVatGroupsAsync(req.Lines.Select(l => l.ItemCode), ct);

        // Calculate totals
        var lines = req.Lines.Select((l, idx) =>
        {
            var effectivePrice = l.UnitPrice * (1 - l.DiscountPercent / 100m);

            // Rounded to money here, not left as a raw product. A fractional quantity — anything
            // weighed — or a discount that does not divide gives a line total with sub-cent digits,
            // and the customer cannot pay those: 1.234 kg at $3.45 came to $4.9173. The column is
            // decimal(18,2), so the database silently rounded it anyway, leaving the total the till
            // was told and the total that was stored two different numbers, with the VAT worked out
            // on a base neither of them kept.
            var lineTotal = Math.Round(l.Quantity * effectivePrice, 2, MidpointRounding.AwayFromZero);
            return new DesktopSaleLineEntity
            {
                LineNum = l.LineNum > 0 ? l.LineNum : idx + 1,
                ItemCode = l.ItemCode,
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                LineTotal = lineTotal,
                WarehouseCode = l.WarehouseCode,
                // The master's answer wins over the request's. Nothing that reaches this handler
                // sends a tax code today, and the day something does, the item master is still the
                // one that decides what an item is taxed at.
                TaxCode = TaxCodeFor(l, vatGroups),
                // Recorded on the line, not just implied by the total, so the basket can be explained
                // afterwards and the receipt can be rebuilt without re-deriving it.
                TaxPercent = tax.RateFor(TaxCodeFor(l, vatGroups)) * 100m,
                DiscountPercent = l.DiscountPercent,
                UoMCode = l.UoMCode
            };
        }).ToList();

        var subtotal = lines.Sum(l => l.LineTotal);

        // At each line's own code's rate. A flat rate across the basket charges VAT on zero-rated
        // and exempt goods — the customer is overcharged, and the receipt declared to ZIMRA says
        // something the basket does not. Rounded once per rate rather than once per line, because
        // that is how the device files it: rounding each line put a cent on sale 5's total that the
        // till never charged and ZIMRA was nonetheless told was paid.
        var vatAmount = tax.VatOnBasket(lines.Select(l => (l.LineTotal, l.TaxCode)));

        var totalAmount = subtotal + vatAmount;

        // Create the sale entity
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = externalRef,

            // What a later request under the same reference is compared against, once the
            // idempotency store's own record has expired and this row is the only guard left.
            RequestHash = requestHash,

            // Decides which route takes this sale to SAP, so it has to be one of the known spellings:
            // a value neither the posting service nor the 18:00 consolidation recognises would leave
            // the sale fiscalised and never invoiced.
            SourceSystem = SaleSourceSystems.NormalizeTillSource(req.SourceSystem),
            CardCode = account.CardCode,
            CardName = req.CardName,
            // Who actually bought, for vending. CardCode above says which business partner sold, so
            // without these a route's takings are one undifferentiated number and no vendor has a
            // history. Snapshotted rather than joined: vendors are renamed, and a sale must keep the
            // name it happened under.
            RouteCustomerId = vendor?.Id,
            RouteCustomerCode = vendor?.Code,
            RouteCustomerName = vendor?.Name,
            DocDate = docDate,
            SalesPersonCode = req.SalesPersonCode,
            NumAtCard = req.NumAtCard,
            Comments = req.Comments,
            TotalAmount = totalAmount,
            VatAmount = vatAmount,
            Currency = req.DocCurrency ?? "ZWG",
            WarehouseCode = account.WarehouseCode,
            CostCentreCode = account.CostCentreCode,
            // Stored in its canonical spelling so reporting can group on it and the posting job can
            // match on it, rather than every till's casing becoming a distinct payment method.
            PaymentMethod = TenderTypes.TryNormalize(req.PaymentMethod, out var tender)
                ? tender
                : req.PaymentMethod,
            PaymentReference = req.PaymentReference,
            AmountPaid = req.AmountPaid,
            CreatedBy = account.UserId.ToString(),
            CreatedAt = DateTime.UtcNow,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Pending,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            Lines = lines
        };

        context.DesktopSales.Add(sale);
        await context.SaveChangesAsync(ct);

        if (!req.Fiscalize)
        {
            sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Skipped;
            await context.SaveChangesAsync(ct);
        }
        else if (SaleSourceSystems.FiscalisesInBackground(sale.SourceSystem))
        {
            // Left Pending for DesktopSaleFiscalisationSweep. Vending prints nothing and has nobody
            // waiting at a counter, so holding the request open while the platform signs buys nothing
            // and costs the operator the wait. The sale cannot reach SAP until it has fiscalised, so
            // the sweep is what completes it.
            await context.SaveChangesAsync(ct);
        }
        else
        {
            // A shop till fiscalises here, in the request. The receipt has to print before the
            // customer walks away, so there is nothing to defer.
            await fiscaliser.FiscaliseAsync(sale, ct);
            await context.SaveChangesAsync(ct);
        }

        var result = new DesktopSaleResponseDto
        {
            SaleId = sale.Id,
            ExternalReferenceId = sale.ExternalReferenceId,
            CardCode = sale.CardCode,
            WarehouseCode = sale.WarehouseCode,
            TotalAmount = sale.TotalAmount,
            VatAmount = sale.VatAmount,
            FiscalizationStatus = sale.FiscalizationStatus.ToString(),
            FiscalReceiptNumber = sale.FiscalReceiptNumber,
            FiscalQRCode = sale.FiscalQRCode,
            FiscalVerificationCode = sale.FiscalVerificationCode,
            FiscalDeviceNumber = sale.FiscalDeviceNumber,
            FiscalDayNo = sale.FiscalDayNo,
            FiscalError = sale.FiscalError,
            CreatedAt = sale.CreatedAt
        };

        // Broadcast real-time event to connected Web clients
        await hubContext.Clients.Group("all").SendAsync("DesktopSaleCreated", new
        {
            sale.Id,
            sale.ExternalReferenceId,
            sale.CardCode,
            sale.CardName,
            sale.TotalAmount,
            sale.WarehouseCode,
            sale.CreatedAt
        });

        return result;
    }

    private static DesktopSaleResponseDto MapToResponse(DesktopSaleEntity sale)
    {
        return new DesktopSaleResponseDto
        {
            SaleId = sale.Id,
            ExternalReferenceId = sale.ExternalReferenceId,
            CardCode = sale.CardCode,
            WarehouseCode = sale.WarehouseCode,
            TotalAmount = sale.TotalAmount,
            VatAmount = sale.VatAmount,
            FiscalizationStatus = sale.FiscalizationStatus.ToString(),
            FiscalReceiptNumber = sale.FiscalReceiptNumber,
            FiscalQRCode = sale.FiscalQRCode,
            FiscalVerificationCode = sale.FiscalVerificationCode,
            FiscalDeviceNumber = sale.FiscalDeviceNumber,
            FiscalDayNo = sale.FiscalDayNo,
            FiscalError = sale.FiscalError,
            CreatedAt = sale.CreatedAt
        };
    }


}
