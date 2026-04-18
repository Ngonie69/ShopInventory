using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;

public sealed class CreateDesktopSaleHandler(
    ApplicationDbContext context,
    IFiscalizationService fiscalizationService,
    IInventoryLockService lockService,
    IOptions<RevmaxSettings> revmaxSettings,
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
        var externalRef = req.ExternalReferenceId ??
            $"DS-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..8]}";

        // Idempotency check
        var existing = await context.DesktopSales
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ExternalReferenceId == externalRef, cancellationToken);

        if (existing != null)
            return Errors.DesktopSales.DuplicateSale(externalRef);

        var today = DateTime.UtcNow.Date;
        var docDate = !string.IsNullOrEmpty(req.DocDate)
            ? DateTime.Parse(req.DocDate).Date
            : today;

        var vatRate = revmaxSettings.Value.VatRate;

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
            return await ValidateDeductAndCreateSaleAsync(
                req, externalRef, today, docDate, vatRate, command.CreatedBy, cancellationToken);
        }
        finally
        {
            // Always release locks
            await lockService.ReleaseMultipleLocksAsync(lockResult.LockTokens);
        }
    }

    private async Task<ErrorOr<DesktopSaleResponseDto>> ValidateDeductAndCreateSaleAsync(
        CreateDesktopSaleRequest req,
        string externalRef,
        DateTime snapshotDate,
        DateTime docDate,
        decimal vatRate,
        string? createdBy,
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

        // Calculate totals
        var lines = req.Lines.Select((l, idx) =>
        {
            var effectivePrice = l.UnitPrice * (1 - l.DiscountPercent / 100m);
            var lineTotal = l.Quantity * effectivePrice;
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
                DiscountPercent = l.DiscountPercent,
                UoMCode = l.UoMCode
            };
        }).ToList();

        var subtotal = lines.Sum(l => l.LineTotal);
        var vatAmount = Math.Round(subtotal * (decimal)vatRate, 2);
        var totalAmount = subtotal + vatAmount;

        // Create the sale entity
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = externalRef,
            SourceSystem = req.SourceSystem ?? "DESKTOP_APP",
            CardCode = req.CardCode,
            CardName = req.CardName,
            DocDate = docDate,
            SalesPersonCode = req.SalesPersonCode,
            NumAtCard = req.NumAtCard,
            Comments = req.Comments,
            TotalAmount = totalAmount,
            VatAmount = vatAmount,
            Currency = req.DocCurrency ?? "ZWG",
            WarehouseCode = req.WarehouseCode,
            PaymentMethod = req.PaymentMethod,
            PaymentReference = req.PaymentReference,
            AmountPaid = req.AmountPaid,
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Pending,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            Lines = lines
        };

        context.DesktopSales.Add(sale);
        await context.SaveChangesAsync(ct);

        // Fiscalize immediately if requested
        if (req.Fiscalize)
        {
            await FiscalizeSaleAsync(sale, ct);
            await context.SaveChangesAsync(ct);
        }
        else
        {
            sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Skipped;
            await context.SaveChangesAsync(ct);
        }

        return new DesktopSaleResponseDto
        {
            SaleId = sale.Id,
            ExternalReferenceId = sale.ExternalReferenceId,
            CardCode = sale.CardCode,
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

    private async Task FiscalizeSaleAsync(DesktopSaleEntity sale, CancellationToken ct)
    {
        try
        {
            // Build an InvoiceDto from local sale data for the fiscalization service
            var invoiceDto = new DTOs.InvoiceDto
            {
                DocNum = sale.Id,
                DocEntry = sale.Id,
                DocDate = sale.DocDate.ToString("yyyy-MM-dd"),
                CardCode = sale.CardCode,
                CardName = sale.CardName,
                DocTotal = sale.TotalAmount,
                VatSum = sale.VatAmount,
                DocCurrency = sale.Currency,
                Comments = sale.Comments,
                Lines = sale.Lines.Select(l => new DTOs.InvoiceLineDto
                {
                    LineNum = l.LineNum,
                    ItemCode = l.ItemCode,
                    ItemDescription = l.ItemDescription,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice,
                    LineTotal = l.LineTotal,
                    WarehouseCode = l.WarehouseCode,
                    DiscountPercent = l.DiscountPercent
                }).ToList()
            };

            // Use the external reference as the invoice number for Revmax
            // (so it's unique and traceable, not the DB Id)
            var result = await fiscalizationService.FiscalizeInvoiceAsync(invoiceDto, cancellationToken: ct);

            if (result.Success && !result.Skipped)
            {
                sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Success;
                sale.FiscalReceiptNumber = result.ReceiptGlobalNo;
                sale.FiscalDeviceNumber = result.DeviceSerial;
                sale.FiscalQRCode = result.QRCode;
                sale.FiscalVerificationCode = result.VerificationCode;
                sale.FiscalDayNo = result.FiscalDayNo;
            }
            else if (result.Skipped)
            {
                sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Skipped;
            }
            else
            {
                sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Failed;
                sale.FiscalError = result.Message ?? result.ErrorDetails;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fiscalization failed for desktop sale {SaleId}", sale.Id);
            sale.FiscalizationStatus = DesktopSaleFiscalizationStatus.Failed;
            sale.FiscalError = ex.Message;
        }
    }
}
