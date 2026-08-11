using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.IngestVanSalesOfflineSales;

/// <summary>
/// Receives a van's backlog of already-completed, already-fiscalised sales and holds them for the
/// end-of-day posting run.
///
/// Three properties matter more than anything else this handler does:
///
///  1. <b>It never fiscalises — it takes custody of a receipt that already is fiscal.</b> Every sale
///     arriving here was stamped on the handset and the customer is holding the printed receipt.
///     Fiscalising again would issue a second ZIMRA receipt for one sale, reversible only by a manual
///     credit note, so the rows are written as <see cref="DesktopSaleFiscalizationStatus.Success"/> and no
///     fiscalisation queue is touched. What it does instead is store the receipt exactly as it was signed
///     and queue it for <c>VanSalesSignedReceiptIngestService</c>, which hands it to the fiscalisation
///     platform. Without that, the receipt would exist only on the handset and in SAP comments, and ZIMRA
///     would close the fiscal day short of the receipts the van actually printed.
///  2. <b>A duplicate is a success.</b> A handset that loses the response re-sends, so re-arrival is
///     routine. The unique index on <c>ExternalReferenceId</c> is the guard, and a duplicate answers
///     <c>duplicate</c> so the handset clears its queue instead of retrying forever.
///  3. <b>One bad row does not fail the batch.</b> A van's backlog is a day's takings; rejecting all of
///     it because one sale references an unassigned customer would strand the rest on the handset.
/// </summary>
public sealed class IngestVanSalesOfflineSalesHandler(
    ApplicationDbContext db,
    ILogger<IngestVanSalesOfflineSalesHandler> logger
) : IRequestHandler<IngestVanSalesOfflineSalesCommand, ErrorOr<VanSalesOfflineSaleBatchResponse>>
{
    private const string StatusAccepted = "accepted";
    private const string StatusDuplicate = "duplicate";
    private const string StatusRejected = "rejected";

    public async Task<ErrorOr<VanSalesOfflineSaleBatchResponse>> Handle(
        IngestVanSalesOfflineSalesCommand command,
        CancellationToken cancellationToken)
    {
        var sales = command.Request.Sales;
        if (sales is null || sales.Count == 0)
        {
            return Error.Validation(
                "VanSalesCompatibility.EmptyBatch",
                "At least one sale is required.");
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var warehouseCode = VanSalesCompatibilityMapper.ResolveAssignedWarehouseCode(user);
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingWarehouse",
                "An assigned warehouse is required for van sales invoicing.");
        }

        var costCentreCode = VanSalesCompatibilityMapper.ResolveAssignedCostCentreCode(user);
        if (string.IsNullOrWhiteSpace(costCentreCode))
        {
            return Error.Validation(
                "VanSalesCompatibility.MissingCostCentre",
                "An assigned cost centre is required for van sales invoicing.");
        }

        var permittedCustomerCodes = await ResolvePermittedCustomerCodesAsync(user, cancellationToken);

        var response = new VanSalesOfflineSaleBatchResponse();

        // Which of these references the database already holds. Read once for the whole batch: a van
        // reconnecting after a day out of coverage sends its entire backlog, and the overlap with what
        // arrived on a previous partially-delivered attempt is usually most of it.
        var references = sales
            .Select(s => s.VanOrder?.Trim())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;

        var existing = await db.DesktopSales
            .AsNoTracking()
            .Where(s => references.Contains(s.ExternalReferenceId))
            .Select(s => new { s.ExternalReferenceId, s.SapDocNum })
            .ToListAsync(cancellationToken);

        var existingByReference = existing.ToDictionary(
            e => e.ExternalReferenceId, e => e.SapDocNum, StringComparer.OrdinalIgnoreCase);

        // Guards against the same reference appearing twice inside one batch, which the unique index
        // would otherwise only catch at SaveChanges — by which point the whole batch fails together.
        var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sale in sales)
        {
            var reference = sale.VanOrder?.Trim();

            if (string.IsNullOrWhiteSpace(reference))
            {
                response.Results.Add(Reject(string.Empty, "van_order is required — it is the idempotency key."));
                response.Rejected++;
                continue;
            }

            if (existingByReference.TryGetValue(reference, out var existingDocNum) || !seenInBatch.Add(reference))
            {
                response.Results.Add(new VanSalesOfflineSaleResultDto
                {
                    VanOrder = reference,
                    Status = StatusDuplicate,
                    Message = "Already received. It is held for posting; do not send it again.",
                    SapDocNum = existingDocNum
                });
                response.Duplicates++;
                continue;
            }

            var validationError = Validate(sale, permittedCustomerCodes);
            if (validationError is not null)
            {
                response.Results.Add(Reject(reference, validationError));
                response.Rejected++;
                continue;
            }

            db.DesktopSales.Add(BuildSale(sale, reference, user, warehouseCode!, costCentreCode!));

            // Accepted either way — the customer paid and the money has to reach SAP — but a sale with no
            // usable signature is a receipt ZIMRA can never be given, and that has to be said out loud
            // rather than left to be discovered when the fiscal day is short.
            var signed = sale.HasSignedReceipt();
            if (!signed)
            {
                logger.LogError(
                    "Van sale {Reference} arrived without a usable device signature, so its ZIMRA receipt " +
                    "cannot be submitted. Receipt {ReceiptGlobalNo}/{ReceiptCounter} on device " +
                    "{FiscalDeviceId}, fiscal day {FiscalDayNo}. The sale is held for posting; the fiscal " +
                    "side needs a person.",
                    reference,
                    sale.ReceiptGlobalNo,
                    sale.ReceiptCounter,
                    sale.FiscalDeviceId,
                    sale.FiscalDayNo);
            }

            response.Results.Add(new VanSalesOfflineSaleResultDto
            {
                VanOrder = reference,
                Status = StatusAccepted,
                Message = signed
                    ? "Held for end-of-day posting; the receipt is queued for ZIMRA."
                    : "Held for end-of-day posting, but it carries no device signature so its receipt " +
                      "cannot be submitted to ZIMRA."
            });
            response.Accepted++;
        }

        if (response.Accepted > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Van sales offline ingest for user {UserId}, warehouse {WarehouseCode}: {Accepted} accepted, {Duplicates} duplicate, {Rejected} rejected.",
            command.UserId,
            warehouseCode,
            response.Accepted,
            response.Duplicates,
            response.Rejected);

        return response;
    }

    private static string? Validate(VanSalesOfflineSaleRequest sale, IReadOnlyCollection<string> permittedCustomerCodes)
    {
        if (sale.Items is null || sale.Items.Count == 0)
        {
            return "At least one line item is required.";
        }

        if (sale.Items.Any(i => string.IsNullOrWhiteSpace(i.Code)))
        {
            return "Every line item needs an item code.";
        }

        if (sale.Items.Any(i => i.Quantity <= 0))
        {
            return "Every line item needs a quantity greater than zero.";
        }

        if (sale.SoldAt == default)
        {
            return "sold_at is required — it decides which trading day the sale posts against.";
        }

        var customerCode = sale.CustomerCode?.Trim();
        if (string.IsNullOrWhiteSpace(customerCode))
        {
            return "customer_code is required.";
        }

        if (!permittedCustomerCodes.Contains(customerCode, StringComparer.OrdinalIgnoreCase))
        {
            return $"Customer {customerCode} is not assigned to this user.";
        }

        // The receipt is already fiscal and its global number is the only durable link back to the ZIMRA
        // receipt the customer holds. Accepting a sale without one would post an invoice that
        // reconciliation can never match to a receipt.
        if (!sale.ReceiptGlobalNo.HasValue || sale.ReceiptGlobalNo.Value <= 0)
        {
            return "receipt_global_no is required — a van sale reaches this endpoint already fiscalised.";
        }

        return null;
    }

    private static DesktopSaleEntity BuildSale(
        VanSalesOfflineSaleRequest sale,
        string reference,
        Models.User user,
        string warehouseCode,
        string costCentreCode)
    {
        var lines = sale.Items.Select((item, index) => new DesktopSaleLineEntity
        {
            LineNum = index,
            ItemCode = item.Code.Trim(),
            ItemDescription = item.Description,
            Quantity = item.Quantity,
            UnitPrice = item.Price,
            LineTotal = Math.Round(item.Price * item.Quantity, 2, MidpointRounding.AwayFromZero),
            WarehouseCode = warehouseCode,
            TaxCode = item.TaxCode,

            // Carried so the signed receipt can be rebuilt for the platform. Order matters as much as the
            // values do — the receipt was signed over these lines in the order they arrived.
            TaxId = item.TaxId,
            TaxPercent = item.TaxPercent,
            HsCode = item.HsCode
        }).ToList();

        return new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = sale.CustomerCode!.Trim(),
            CardName = sale.CustomerName,
            // The trading day is the handset's, not the server's. A sale made near midnight and uploaded
            // the next morning belongs to the day it was sold on, which is also the fiscal day its ZIMRA
            // receipt was stamped into.
            DocDate = sale.SoldAt.Date,
            NumAtCard = reference,
            TotalAmount = sale.Total,
            VatAmount = sale.VatAmount,
            Currency = string.IsNullOrWhiteSpace(sale.Currency) ? "USD" : sale.Currency.Trim(),

            // Already stamped on the handset. Never re-fiscalise: the customer is holding the receipt.
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            FiscalDeviceNumber = sale.FiscalDeviceId,
            FiscalDayNo = sale.FiscalDayNo?.ToString(),
            ReceiptGlobalNo = sale.ReceiptGlobalNo,
            ReceiptCounter = sale.ReceiptCounter,
            FiscalVerificationCode = sale.VerificationCode,
            FiscalQRCode = sale.QrCode,
            FiscalReceiptNumber = sale.ReceiptGlobalNo?.ToString(),

            // The signed receipt, stored verbatim so it can be handed to the fiscalisation platform and,
            // through it, to ZIMRA. Nothing here is recomputed: the signature covers these exact values.
            ReceiptDate = sale.ReceiptDate,
            FiscalDayOpenedAt = sale.FiscalDayOpenedAt,
            PreviousReceiptHash = sale.PreviousReceiptHash?.Trim(),
            DeviceSignatureHash = sale.DeviceSignatureHash?.Trim(),
            DeviceSignatureValue = sale.DeviceSignatureValue?.Trim(),
            ReceiptIngestStatus = sale.HasSignedReceipt()
                ? DesktopSaleReceiptIngestStatus.Pending
                : DesktopSaleReceiptIngestStatus.Unsignable,

            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            WarehouseCode = warehouseCode,
            CostCentreCode = costCentreCode,
            PaymentMethod = sale.PaymentMethod,
            PaymentReference = sale.PaymentReference,
            AmountPaid = sale.AmountPaid,
            CreatedBy = user.Id.ToString(),
            CreatedAt = DateTime.UtcNow,
            Lines = lines
        };
    }

    private async Task<IReadOnlyCollection<string>> ResolvePermittedCustomerCodesAsync(
        Models.User user,
        CancellationToken cancellationToken)
    {
        // A route-customer van invoices its own business partner account rather than the individual shop,
        // exactly as the online path does in CreateVanSalesDirectInvoiceHandler.
        if (VanSalesRouteCustomerScope.UsesLocalRouteCustomers(user))
        {
            var assigned = user.AssignedBusinessPartnerCode?.Trim();
            return string.IsNullOrWhiteSpace(assigned)
                ? []
                : new HashSet<string>([assigned], StringComparer.OrdinalIgnoreCase);
        }

        var effectiveCustomerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
            db, user, logger, cancellationToken);

        return effectiveCustomerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static VanSalesOfflineSaleResultDto Reject(string reference, string message) => new()
    {
        VanOrder = reference,
        Status = StatusRejected,
        Message = message
    };
}
