using System.Globalization;
using ErrorOr;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;

/// <summary>
/// Asks SAP, before a till sale is taken, whether it will accept the invoice that sale becomes.
/// </summary>
/// <remarks>
/// <para><b>Why the ledger is not enough.</b> The till is gated on the stock ledger, and the ledger is a
/// copy of SAP: stock moved in B1 reaches it only when something re-reads SAP, and the hourly
/// reconciliation only re-reads items the ledger has already sold that day. A sale the ledger allowed
/// was then fiscalised in the request, and only afterwards did the posting job pick its batches against
/// SAP and find them short. KEF-FAC-20260915-8F904ECE7543 sold 22 of an item SAP held 21 of, printed a
/// ZIMRA receipt, and was refused six times. Nothing after the receipt can undo it.</para>
///
/// <para><b>The same allocation the post runs.</b> This is <c>InvoiceBatchAllocation</c>'s own call —
/// FEFO, batch- and serial-managed lines only — over the lines the post will build, so "passes here"
/// and "allocates at posting" are one rule rather than two that can drift. Non-batch lines are not read,
/// for the same reason the post does not read them: SAP needs no selection for them.</para>
///
/// <para><b>Less what SAP has not been told.</b> A till sale reaches SAP up to a minute after it is
/// taken, and a refused one not until someone fixes it. Until then SAP still shows its units, and they
/// will be taken by that sale's post before this one's. So every unposted till sale of the same item is
/// set aside first (<see cref="UnpostedTillSales"/>). Returned credit units are deliberately not added
/// back: SAP only gets them when the credit memo posts, which can be after this sale, or never.</para>
///
/// <para><b>An unreadable SAP refuses.</b> A check that passes whenever it cannot look is the gap it
/// exists to close. A read that fails, or does not answer within
/// <see cref="DailyStockSettings.CounterSapCheckSeconds"/>, refuses the sale and says why.</para>
///
/// <para>Runs inside the sale's inventory locks, so two tills cannot both be told the last units are
/// theirs, and before the ledger commit, so a refusal here has nothing to give back.</para>
/// </remarks>
public sealed class CounterSapStockCheck(
    ApplicationDbContext context,
    IBatchInventoryValidationService batchValidation,
    IStockLedger stockLedger,
    IOptions<DailyStockSettings> dailyStock,
    ILogger<CounterSapStockCheck> logger)
{
    public async Task<ErrorOr<Success>> CheckAsync(
        IReadOnlyList<CreateDesktopSaleLineRequest> lines,
        string warehouseCode,
        string reference,
        CancellationToken cancellationToken)
    {
        var settings = dailyStock.Value;

        if (!settings.CheckSapStockAtCounter)
        {
            logger.LogWarning(
                "Sale {Reference} was not checked against SAP stock: DailyStock:CheckSapStockAtCounter is off",
                reference);
            return Result.Success;
        }

        var request = BuildRequest(lines, warehouseCode);
        var seconds = Math.Max(1, settings.CounterSapCheckSeconds);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(seconds));

        BatchAllocationResult allocation;
        Dictionary<string, decimal> unposted;

        try
        {
            unposted = await UnpostedTillSales.OutstandingAsync(
                context,
                warehouseCode,
                stockLedger.CurrentLedgerDay,
                settings.StockFetchTimeCAT,
                settings.UnpostedSaleLookbackDays,
                logger,
                budget.Token,
                netReturnedCredits: false);

            allocation = await batchValidation.ValidateAndAllocateBatchesAsync(
                request,
                autoAllocate: true,
                BatchAllocationStrategy.FEFO,
                budget.Token,
                checkNonBatchStock: false,
                claimedAhead: ClaimsFor(request, unposted, warehouseCode));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Refused sale {Reference}: SAP stock for {Warehouse} did not answer within {Seconds}s",
                reference, warehouseCode, seconds);

            return Errors.DesktopSales.SapStockUnreadable(
                $"SAP did not answer within {seconds} seconds, so this sale could not be checked against "
                + "SAP's stock and nothing was sold. Try again shortly.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Refused sale {Reference}: SAP stock for {Warehouse} could not be read", reference, warehouseCode);

            return Errors.DesktopSales.SapStockUnreadable(
                $"SAP could not be read, so this sale could not be checked against SAP's stock and nothing "
                + $"was sold. Try again shortly. ({ex.Message})");
        }

        if (allocation.IsValid)
        {
            return Result.Success;
        }

        logger.LogWarning(
            "Refused sale {Reference} at the counter; SAP would not allocate it: {Errors}",
            reference,
            string.Join("; ", allocation.ValidationErrors.Select(error => error.Message)));

        if (allocation.ValidationErrors.Any(error => error.ErrorCode == BatchValidationErrorCode.StockUnknown))
        {
            return Errors.DesktopSales.SapStockUnreadable(
                "SAP's stock could not be read, so this sale could not be checked and nothing was sold. "
                + "Try again shortly.");
        }

        return Errors.DesktopSales.SapStockShort(DescribeShortfall(allocation, unposted));
    }

    /// <summary>
    /// The lines the post will send, as <c>DesktopSaleInvoiceRequestBuilder</c> builds them: the same
    /// items, quantities, warehouses and units, in the same order, asking for batches to be chosen.
    /// </summary>
    internal static CreateInvoiceRequest BuildRequest(
        IReadOnlyList<CreateDesktopSaleLineRequest> lines,
        string warehouseCode) => new()
    {
        Lines = lines
            .Select(line => new CreateInvoiceLineRequest
            {
                ItemCode = line.ItemCode,
                Quantity = line.Quantity,
                WarehouseCode = string.IsNullOrWhiteSpace(line.WarehouseCode) ? warehouseCode : line.WarehouseCode,
                UoMCode = line.UoMCode,
                AutoAllocateBatches = true,
                BatchNumbers = null
            })
            .ToList()
    };

    /// <summary>
    /// Unposted quantities for the items on this sale only, and only where something is outstanding.
    /// </summary>
    internal static IReadOnlyList<StockLedgerLine> ClaimsFor(
        CreateInvoiceRequest request,
        IReadOnlyDictionary<string, decimal> unposted,
        string warehouseCode) =>
        (request.Lines ?? [])
            .Select(line => line.ItemCode ?? string.Empty)
            .Where(itemCode => itemCode.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(itemCode => new StockLedgerLine(itemCode, warehouseCode, unposted.GetValueOrDefault(itemCode)))
            .Where(claim => claim.Quantity > 0)
            .ToList();

    /// <summary>
    /// What the cashier reads. Item by item, in quantities, and saying outright that nothing was sold —
    /// the till puts this in front of the person at the counter as the reason.
    /// </summary>
    internal static string DescribeShortfall(
        BatchAllocationResult allocation,
        IReadOnlyDictionary<string, decimal> unposted)
    {
        var parts = allocation.ValidationErrors
            .Select(error =>
            {
                if (error.ErrorCode is not (BatchValidationErrorCode.InsufficientTotalStock
                                            or BatchValidationErrorCode.InsufficientBatchQuantity))
                {
                    return $"{error.ItemCode}: {error.Message}";
                }

                var pending = unposted.GetValueOrDefault(error.ItemCode);
                var pendingNote = pending > 0
                    ? $" once the {Quantity(pending)} already sold but not yet in SAP are taken off"
                    : string.Empty;

                return $"{error.ItemCode}: {Quantity(error.RequestedQuantity)} on this sale, only "
                    + $"{Quantity(Math.Max(0, error.AvailableQuantity))} left in SAP at {error.WarehouseCode}{pendingNote}";
            })
            .Distinct();

        return "SAP does not hold enough stock for this sale, so nothing was sold. "
            + string.Join("; ", parts) + ".";
    }

    private static string Quantity(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
