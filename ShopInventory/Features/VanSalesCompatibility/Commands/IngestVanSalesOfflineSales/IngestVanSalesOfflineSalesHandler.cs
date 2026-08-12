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
///  4. <b>Who bought is recorded separately from who is billed.</b> A route-customer van invoices its own
///     business partner, so <c>CardCode</c> is the same on every sale it makes and cannot answer "what did
///     this shop buy". The route customer named in <c>customer_code</c> is resolved and stored alongside
///     it. That code was previously rejected outright — only the posting account was accepted — which
///     both lost the attribution and stranded the takings of any handset that reported the shop. Where
///     the account is sent instead, the sale's <c>customer_name</c> is tried and used only if exactly one
///     customer on the route answers to it; anything less certain is left unattributed rather than
///     guessed, because a sale credited to the wrong shop is worse than one credited to none.
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

        var resolveCustomer = await BuildCustomerResolverAsync(user, cancellationToken);

        var response = new VanSalesOfflineSaleBatchResponse();

        // How the batch attributed, counted rather than logged one by one. A van that names its own
        // business partner on every sale is a handset on an older build: the takings are right either
        // way, but whether the per-customer report can see them is worth saying once per upload.
        var unattributed = 0;
        var matchedByName = 0;

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

            var validationError = Validate(sale, resolveCustomer, out var customer);
            if (validationError is not null)
            {
                response.Results.Add(Reject(reference, validationError));
                response.Rejected++;
                continue;
            }

            if (VanSalesRouteCustomerScope.UsesLocalRouteCustomers(user))
            {
                if (customer!.RouteCustomer is null)
                {
                    unattributed++;
                }
                else if (!string.Equals(
                             customer.RouteCustomerCode,
                             sale.CustomerCode?.Trim(),
                             StringComparison.OrdinalIgnoreCase))
                {
                    // The shop was found by name, not by the code that arrived — so the handset is still
                    // sending the posting account. Counted apart from the outright failures because it
                    // is the same underlying gap and the fix is the same one.
                    matchedByName++;
                }
            }

            db.DesktopSales.Add(BuildSale(sale, reference, customer, user, warehouseCode!, costCentreCode!));

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

        if (unattributed > 0 || matchedByName > 0)
        {
            logger.LogWarning(
                "{Unattributed} of {Accepted} sales in this batch could not be attributed to a shop and " +
                "{MatchedByName} were attributed by customer_name alone. All of them named the van's " +
                "business partner {BusinessPartnerCode} in customer_code, so the handset is sending the " +
                "posting account rather than the route customer's own code. The name fallback is a " +
                "bridge, not a fix — it goes quiet the moment two shops on a route share a name.",
                unattributed,
                response.Accepted,
                matchedByName,
                user.AssignedBusinessPartnerCode);
        }

        return response;
    }

    private static string? Validate(
        VanSalesOfflineSaleRequest sale,
        Func<string, string?, VanSalesCustomerResolution?> resolveCustomer,
        out VanSalesCustomerResolution? customer)
    {
        customer = null;

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

        customer = resolveCustomer(customerCode, sale.CustomerName);
        if (customer is null)
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
        VanSalesCustomerResolution customer,
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
            // The account SAP bills, which for a route-customer van is the van's own business partner
            // whatever the handset put in customer_code.
            CardCode = customer.PostingCardCode,
            CardName = sale.CustomerName,
            // The shop. All three or none: a name without a code would read to the report as a customer
            // that was deleted, when in fact none was ever identified. When the handset names the posting
            // account instead of a route customer the sale still stands, it just cannot be attributed,
            // and whatever it called the customer stays in CardName above.
            RouteCustomerId = customer.RouteCustomerId,
            RouteCustomerCode = customer.RouteCustomerCode,
            RouteCustomerName = customer.RouteCustomerName,
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

    /// <summary>
    /// Turns a sale's <c>customer_code</c> and <c>customer_name</c> into the two parties it needs: the
    /// account it posts against, and the shop it was sold to. Returns null if the code is not one this
    /// van may sell to.
    ///
    /// Built once for the batch — a van's backlog is a day of trading against a handful of customers, and
    /// the assigned list is the same for every row in it.
    ///
    /// For a route-customer van the code may be either the van's own business partner or one of its route
    /// customers. Both are accepted: the online path hands the handset a shop list carrying both, and
    /// rejecting either would strand a day's takings on the device.
    ///
    /// Only the route customer's own code identifies the shop outright. Where the account was sent
    /// instead, the name is tried — see <see cref="MatchByName"/> — and where that cannot answer safely
    /// the sale is still accepted, still posts, and reports as unattributed.
    /// </summary>
    private async Task<Func<string, string?, VanSalesCustomerResolution?>> BuildCustomerResolverAsync(
        Models.User user,
        CancellationToken cancellationToken)
    {
        if (VanSalesRouteCustomerScope.UsesLocalRouteCustomers(user))
        {
            var postingCardCode = user.AssignedBusinessPartnerCode?.Trim();
            if (string.IsNullOrWhiteSpace(postingCardCode))
            {
                return (_, _) => null;
            }

            var routeCustomers = await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(
                db, user, cancellationToken);

            var byCode = routeCustomers
                .GroupBy(customer => customer.Code.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            return (code, name) =>
            {
                if (byCode.TryGetValue(code, out var routeCustomer))
                {
                    return new VanSalesCustomerResolution(postingCardCode, routeCustomer);
                }

                if (!string.Equals(code, postingCardCode, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return new VanSalesCustomerResolution(postingCardCode, MatchByName(routeCustomers, name));
            };
        }

        var effectiveCustomerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
            db, user, logger, cancellationToken);

        var permitted = effectiveCustomerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // These customers are real business partners, so the posting account is the customer and there is
        // no route customer to record.
        return (code, _) => permitted.TryGetValue(code, out var permittedCode)
            ? new VanSalesCustomerResolution(permittedCode, null)
            : null;
    }

    /// <summary>
    /// Last resort for a handset that reported the posting account rather than the shop: find the shop by
    /// the name the sale carries.
    ///
    /// Worth doing because the alternative is a whole route's history arriving as one undivided figure,
    /// and because the name on the sale is the route customer's own — the app copies it from the same
    /// record it took the code from.
    ///
    /// <b>Only where exactly one customer on the route answers to it.</b> Names are free text, editable,
    /// and not unique within a route. Two shops called "Tuck Shop" would make any choice between them a
    /// coin toss recorded as fact, and a sale credited to the wrong shop is worse than one credited to
    /// none: nobody goes looking for a figure that is already there. Ambiguity therefore returns null and
    /// the sale reports as unattributed, which is true.
    /// </summary>
    private static RouteCustomerEntity? MatchByName(
        IReadOnlyCollection<RouteCustomerEntity> routeCustomers,
        string? name)
    {
        var trimmedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            return null;
        }

        var matches = routeCustomers
            .Where(customer => string.Equals(customer.Name.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static VanSalesOfflineSaleResultDto Reject(string reference, string message) => new()
    {
        VanOrder = reference,
        Status = StatusRejected,
        Message = message
    };
}
