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
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;

public sealed class CreateDesktopSaleHandler(
    ApplicationDbContext context,
    DesktopSaleFiscaliser fiscaliser,
    IInventoryLockService lockService,
    IHubContext<NotificationHub> hubContext,
    IIdempotencyRequestStore idempotencyRequestStore,
    IOptions<TaxSettings> taxSettings,
    ILogger<CreateDesktopSaleHandler> logger
) : IRequestHandler<CreateDesktopSaleCommand, ErrorOr<DesktopSaleResponseDto>>
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(30);

    public async Task<ErrorOr<DesktopSaleResponseDto>> Handle(
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
                        return acquireResult.Response;
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

                return existingResponse;
            }

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
                    req, externalRef, today, docDate, account, vendor, cancellationToken);

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

                return result;
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

    private async Task<ErrorOr<DesktopSaleResponseDto>> ValidateDeductAndCreateSaleAsync(
        CreateDesktopSaleRequest req,
        string externalRef,
        DateTime snapshotDate,
        DateTime docDate,
        SellingAccountAssignments account,
        RouteCustomerEntity? vendor,
        CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            // Validate stock from local snapshot
            var stockErrors = await ValidateLocalStockAsync(snapshotDate, req, ct);
            if (stockErrors.Count > 0)
                return stockErrors.First();

            // Deduct stock from snapshot (with optimistic concurrency)
            try
            {
                await DeductStockFromSnapshotAsync(snapshotDate, req, ct);
                break; // Success — proceed to create sale
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxRetries)
            {
                logger.LogWarning(
                    "Concurrency conflict on stock deduction for {Ref}, attempt {Attempt}/{Max}. Retrying...",
                    externalRef, attempt, MaxRetries);

                // Detach stale tracked entities so the retry re-reads fresh rows
                foreach (var entry in context.ChangeTracker.Entries<DailyStockSnapshotItemEntity>())
                    entry.State = EntityState.Detached;
            }
            catch (DbUpdateConcurrencyException)
            {
                return Errors.DesktopSales.ConcurrencyConflict;
            }
        }

        var tax = taxSettings.Value;

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
                TaxCode = l.TaxCode,
                // Recorded on the line, not just implied by the total, so the basket can be explained
                // afterwards and the receipt can be rebuilt without re-deriving it.
                TaxPercent = tax.RateFor(l.TaxCode) * 100m,
                DiscountPercent = l.DiscountPercent,
                UoMCode = l.UoMCode
            };
        }).ToList();

        var subtotal = lines.Sum(l => l.LineTotal);

        // Per line, at its own code's rate. A flat rate across the basket charges VAT on zero-rated
        // and exempt goods — the customer is overcharged, and the receipt declared to ZIMRA says
        // something the basket does not.
        var vatAmount = lines.Sum(l => tax.VatOn(l.LineTotal, l.TaxCode));
        var totalAmount = subtotal + vatAmount;

        // Create the sale entity
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = externalRef,
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
            FiscalError = sale.FiscalError,
            CreatedAt = sale.CreatedAt
        };
    }

    private async Task<List<Error>> ValidateLocalStockAsync(
        DateTime snapshotDate, CreateDesktopSaleRequest req, CancellationToken ct)
    {
        var errors = new List<Error>();

        // Group lines by item+warehouse to aggregate quantities
        var grouped = req.Lines
            .GroupBy(l => new { l.ItemCode, l.WarehouseCode })
            .Select(g => new { g.Key.ItemCode, g.Key.WarehouseCode, TotalQty = g.Sum(l => l.Quantity) });

        foreach (var item in grouped)
        {
            var available = await context.DailyStockSnapshotItems
                .Where(i => i.Snapshot.SnapshotDate == snapshotDate &&
                            i.ItemCode == item.ItemCode &&
                            i.WarehouseCode == item.WarehouseCode)
                .SumAsync(i => i.AvailableQuantity, ct);

            if (available < item.TotalQty)
            {
                errors.Add(Errors.DesktopSales.InsufficientStock(
                    item.ItemCode, item.WarehouseCode, item.TotalQty, available));
            }
        }

        return errors;
    }

    private async Task DeductStockFromSnapshotAsync(
        DateTime snapshotDate, CreateDesktopSaleRequest req, CancellationToken ct)
    {
        foreach (var line in req.Lines)
        {
            var remaining = line.Quantity;

            var snapshotItems = await context.DailyStockSnapshotItems
                .Where(i => i.Snapshot.SnapshotDate == snapshotDate &&
                            i.ItemCode == line.ItemCode &&
                            i.WarehouseCode == line.WarehouseCode &&
                            i.AvailableQuantity > 0)
                .OrderBy(i => i.ExpiryDate) // FEFO
                .ToListAsync(ct);

            foreach (var item in snapshotItems)
            {
                if (remaining <= 0) break;
                var deduct = Math.Min(item.AvailableQuantity, remaining);
                item.AvailableQuantity -= deduct;
                remaining -= deduct;
            }
        }

        await context.SaveChangesAsync(ct);
    }
}
