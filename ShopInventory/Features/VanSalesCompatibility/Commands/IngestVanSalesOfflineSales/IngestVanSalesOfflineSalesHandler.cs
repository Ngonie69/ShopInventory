using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using System.Globalization;

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
///     credit note, so any sale that took a number off its device's chain is written as
///     <see cref="DesktopSaleFiscalizationStatus.Success"/> and no fiscalisation queue is touched. A sale
///     from a handset too old to stamp is the exception and is written as
///     <see cref="DesktopSaleFiscalizationStatus.Failed"/>: nothing was printed, so calling it a success
///     would hide an unfiscalised sale and disable the one control that could still fix it. What it does
///     instead is store the receipt exactly as it was signed
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
    IAuditService auditService,
    Microsoft.Extensions.Options.IOptions<Configuration.FiscalisationSettings> fiscalisationOptions,
    ILogger<IngestVanSalesOfflineSalesHandler> logger
) : IRequestHandler<IngestVanSalesOfflineSalesCommand, ErrorOr<VanSalesOfflineSaleBatchResponse>>
{
    private const string StatusAccepted = "accepted";
    private const string StatusDuplicate = "duplicate";
    private const string StatusRejected = "rejected";

    /// <summary>
    /// The audit entity a batch is filed under. The id is the van's warehouse, because "show me every
    /// upload this van has made" is the question an investigation actually asks — a batch has no
    /// identity of its own to key on.
    /// </summary>
    private const string BatchEntityType = "VanSalesOfflineSaleBatch";

    private const string SaleEntityType = "VanSalesOfflineSale";

    /// <summary>
    /// How many individual sales one upload may write its own audit row for.
    ///
    /// Each row is a separate round trip, and a misconfigured van can reject an entire day at once. Past
    /// this point the references are folded into the batch row instead, which is still searchable — the
    /// audit trail's search covers the details text.
    /// </summary>
    private const int MaxPerSaleAuditRows = 25;

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

        // Every refusal below turns a van away with its whole day still on the handset, and the rep sees
        // only a failed upload. Each one is recorded so that a van which has stopped delivering its
        // takings can be found from the audit trail rather than from a phone call.
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            await AuditRefusedBatchAsync(
                sales.Count,
                entityId: command.UserId.ToString(),
                reason: "the user is unknown or deactivated");

            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var warehouseCode = VanSalesCompatibilityMapper.ResolveAssignedWarehouseCode(user);
        if (string.IsNullOrWhiteSpace(warehouseCode))
        {
            await AuditRefusedBatchAsync(
                sales.Count,
                entityId: null,
                reason: "the user has no assigned warehouse");

            return Error.Validation(
                "VanSalesCompatibility.MissingWarehouse",
                "An assigned warehouse is required for van sales invoicing.");
        }

        var costCentreCode = VanSalesCompatibilityMapper.ResolveAssignedCostCentreCode(user);
        if (string.IsNullOrWhiteSpace(costCentreCode))
        {
            await AuditRefusedBatchAsync(
                sales.Count,
                entityId: warehouseCode,
                reason: "the user has no assigned cost centre");

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

        // The two outcomes worth naming one by one afterwards. A rejected sale is the only one that is
        // never written anywhere — it exists solely in the reply the handset is about to receive, so if
        // that reply is lost the takings vanish with no trace of what they were or why they bounced.
        var rejections = new List<(string Reference, string Reason)>();
        var unsignable = new List<string>();

        // What the accepted sales are worth, kept per currency because a route may trade in two.
        var acceptedValue = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

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
                const string reason = "van_order is required — it is the idempotency key.";
                response.Results.Add(Reject(string.Empty, reason));
                response.Rejected++;
                rejections.Add((Reference: "(no reference)", Reason: reason));
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
                rejections.Add((reference, validationError));
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

            var signed = sale.HasSignedReceipt();
            var stamped = sale.ClaimsReceiptSequence();

            // The one case worth refusing outright, and only once the fleet can all stamp. Until then
            // refusing would stop a van trading over a handset build its driver cannot change, which
            // costs real takings and makes nobody compliant.
            if (!stamped && fiscalisationOptions.Value.RequireStampedVanSales)
            {
                const string unstampedRefusal =
                    "This sale carries no fiscal receipt. Stamped receipts are now required, so it cannot " +
                    "be accepted — update the handset to a build that signs receipts.";

                rejections.Add((reference, unstampedRefusal));
                response.Results.Add(new VanSalesOfflineSaleResultDto
                {
                    VanOrder = reference,
                    Status = StatusRejected,
                    Message = unstampedRefusal
                });
                response.Rejected++;

                logger.LogError(
                    "Van sale {Reference} was refused: it carries no fiscal receipt and " +
                    "Fiscalisation:RequireStampedVanSales is on. This handset is on a build older than the " +
                    "signing release and cannot trade until it is updated.",
                    reference);
                continue;
            }

            // customer is set whenever Validate returns no error, but that is a relationship the
            // compiler cannot see through an out parameter.
            db.DesktopSales.Add(BuildSale(sale, reference, customer!, user, warehouseCode!, costCentreCode!));

            // Accepted either way — the customer paid and the money has to reach SAP — but a receipt ZIMRA
            // can never be given has to be said out loud rather than discovered when the fiscal day comes
            // up short. The two ways that happens need different words and have different consequences.
            if (!signed)
            {
                unsignable.Add(reference);

                if (stamped)
                {
                    logger.LogError(
                        "Van sale {Reference} arrived without a usable device signature, so its ZIMRA receipt " +
                        "cannot be submitted. Receipt {ReceiptGlobalNo}/{ReceiptCounter} on device " +
                        "{FiscalDeviceId}, fiscal day {FiscalDayNo}. Its number is spent, so every later " +
                        "receipt from this handset is blocked behind it. The sale is held for posting; the " +
                        "fiscal side needs a person.",
                        reference,
                        sale.ReceiptGlobalNo,
                        sale.ReceiptCounter,
                        sale.FiscalDeviceId,
                        sale.FiscalDayNo);
                }
                else
                {
                    logger.LogWarning(
                        "Van sale {Reference} on device {FiscalDeviceId} was never stamped — the handset is on " +
                        "a build older than the signing release. The sale is held for posting and nothing else " +
                        "is blocked, but the customer has no fiscal receipt and ZIMRA has no record. Update " +
                        "this handset, then turn on Fiscalisation:RequireStampedVanSales once the fleet is done.",
                        reference,
                        sale.FiscalDeviceId);
                }
            }

            response.Results.Add(new VanSalesOfflineSaleResultDto
            {
                VanOrder = reference,
                Status = StatusAccepted,
                Message = signed
                    ? "Held for end-of-day posting; the receipt is queued for ZIMRA."
                    : stamped
                        ? "Held for end-of-day posting, but it carries no device signature so its receipt " +
                          "cannot be submitted to ZIMRA."
                        : "Held for end-of-day posting. This handset stamped no fiscal receipt, so the sale " +
                          "has no ZIMRA record — update the handset."
            });
            response.Accepted++;

            var currency = string.IsNullOrWhiteSpace(sale.Currency) ? "USD" : sale.Currency.Trim();
            acceptedValue[currency] = acceptedValue.GetValueOrDefault(currency) + sale.Total;
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

        await WriteAuditTrailAsync(
            warehouseCode!,
            response,
            acceptedValue,
            rejections,
            unsignable,
            unattributed,
            matchedByName);

        return response;
    }

    /// <summary>
    /// Records what the upload did, after the sales themselves are safely committed.
    ///
    /// The ordering is not incidental. <c>AuditService</c> writes through the same
    /// <see cref="ApplicationDbContext"/> this handler is adding sales to, so an audit call made mid-loop
    /// would call <c>SaveChanges</c> on a half-built batch and commit rows the loop had not finished
    /// deciding about. Everything here runs once the batch is settled.
    /// </summary>
    private async Task WriteAuditTrailAsync(
        string warehouseCode,
        VanSalesOfflineSaleBatchResponse response,
        IReadOnlyDictionary<string, decimal> acceptedValue,
        IReadOnlyList<(string Reference, string Reason)> rejections,
        IReadOnlyList<string> unsignable,
        int unattributed,
        int matchedByName)
    {
        try
        {
            // Rejections first: they are the outcome most likely to need acting on, and if the cap bites
            // it should bite the merely-unsubmittable rather than the outright lost.
            var perSaleBudget = MaxPerSaleAuditRows;

            foreach (var (reference, reason) in rejections.Take(perSaleBudget))
            {
                await auditService.LogAsync(
                    Models.AuditActions.RejectVanSalesOfflineSale,
                    SaleEntityType,
                    reference,
                    $"Van {warehouseCode} offline sale {reference} was refused and not stored: {reason}",
                    false,
                    reason);
            }

            perSaleBudget -= Math.Min(rejections.Count, perSaleBudget);

            foreach (var reference in unsignable.Take(perSaleBudget))
            {
                await auditService.LogAsync(
                    Models.AuditActions.UnsignableVanSalesOfflineSale,
                    SaleEntityType,
                    reference,
                    $"Van {warehouseCode} offline sale {reference} was stored for posting but carries no " +
                    "device signature, so its ZIMRA receipt cannot be submitted.",
                    false,
                    "No usable device signature.");
            }

            var truncated = rejections.Count + unsignable.Count > MaxPerSaleAuditRows;

            await auditService.LogAsync(
                Models.AuditActions.IngestVanSalesOfflineBatch,
                BatchEntityType,
                warehouseCode,
                BuildBatchDetails(
                    warehouseCode, response, acceptedValue, rejections, unsignable,
                    unattributed, matchedByName, truncated),
                response.Rejected == 0 && unsignable.Count == 0,
                BuildBatchError(response.Rejected, unsignable.Count));
        }
        catch (Exception ex)
        {
            // The takings are already committed. Losing the audit trail for them is bad, but failing the
            // upload over it would send the van away with a day's sales it has no way to re-send.
            logger.LogWarning(ex, "Failed to audit the van sales offline batch for {WarehouseCode}.", warehouseCode);
        }
    }

    private async Task AuditRefusedBatchAsync(int saleCount, string? entityId, string reason)
    {
        try
        {
            await auditService.LogAsync(
                Models.AuditActions.IngestVanSalesOfflineBatch,
                BatchEntityType,
                entityId,
                $"Refused an upload of {saleCount} offline {Plural(saleCount, "sale")} because {reason}. " +
                "The sales stay on the handset.",
                false,
                reason);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit a refused van sales offline batch.");
        }
    }

    private static string BuildBatchDetails(
        string warehouseCode,
        VanSalesOfflineSaleBatchResponse response,
        IReadOnlyDictionary<string, decimal> acceptedValue,
        IReadOnlyList<(string Reference, string Reason)> rejections,
        IReadOnlyList<string> unsignable,
        int unattributed,
        int matchedByName,
        bool truncated)
    {
        var total = response.Accepted + response.Duplicates + response.Rejected;
        var parts = new List<string>
        {
            $"Van {warehouseCode} uploaded {total} offline {Plural(total, "sale")}: " +
            $"{response.Accepted} accepted, {response.Duplicates} duplicate, {response.Rejected} rejected."
        };

        if (acceptedValue.Count > 0)
        {
            var value = string.Join(
                " + ",
                acceptedValue
                    .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => $"{entry.Value.ToString("N2", CultureInfo.InvariantCulture)} {entry.Key}"));

            parts.Add($"Accepted value {value}.");
        }

        if (unattributed > 0 || matchedByName > 0)
        {
            parts.Add(
                $"{unattributed} could not be attributed to a shop and {matchedByName} were matched by " +
                "customer name alone.");
        }

        if (unsignable.Count > 0)
        {
            parts.Add(
                $"{unsignable.Count} accepted without a device signature, so {Plural(unsignable.Count, "receipt")} " +
                "cannot reach ZIMRA.");
        }

        if (rejections.Count > 0)
        {
            // Named in full even past the per-sale row cap: this is what makes a lost sale findable by
            // its reference when there was no room to give it a row of its own.
            parts.Add(
                "Rejected: " +
                string.Join("; ", rejections.Select(r => $"{r.Reference} ({r.Reason})")));
        }

        if (truncated)
        {
            parts.Add($"Only the first {MaxPerSaleAuditRows} sales were given rows of their own.");
        }

        return string.Join(" ", parts);
    }

    private static string? BuildBatchError(int rejected, int unsignable)
    {
        if (rejected == 0 && unsignable == 0)
        {
            return null;
        }

        var reasons = new List<string>();

        if (rejected > 0)
        {
            reasons.Add($"{rejected} {Plural(rejected, "sale")} rejected and left on the handset");
        }

        if (unsignable > 0)
        {
            reasons.Add($"{unsignable} {Plural(unsignable, "receipt")} cannot be submitted to ZIMRA");
        }

        return string.Join("; ", reasons) + ".";
    }

    private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";

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

        var entity = new DesktopSaleEntity
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

        // The fiscal half, shared with the online path so a cart is judged the same either way.
        entity.ApplySignedReceipt(
            sale,
            "The handset stamped no fiscal receipt for this sale. It is on a build older than the " +
            "signing release, so nothing was printed for the customer and ZIMRA has no record.");

        return entity;
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
