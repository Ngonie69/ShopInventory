using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Mobile;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.CreateInvoiceDirect;
using ShopInventory.Features.ExceptionCenter;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesDirectInvoice;

/// <summary>
/// A van sale made with signal: the invoice posts to SAP inside the request, and the rep waits for it.
/// </summary>
/// <remarks>
/// Since every handset gained a ZIMRA device this path also has a fiscal obligation, and it is the one
/// obligation that cannot be met later. The sale itself is safe the moment SAP accepts it — the money is
/// in a real invoice — but the receipt the handset signed exists only in the request that carried it and
/// on the device. If it is not stored here it is stored nowhere, the fiscalisation platform is never given
/// it, and the device's fiscal day closes short of a receipt whose number was spent. FDMS reconciles a day
/// against a contiguous chain, so a hole in it is not one missing sale, it is a day that cannot close.
///
/// <para>
/// The receipt is written as a <c>DesktopSaleEntity</c> under
/// <see cref="SaleSourceSystems.VanSalesOnline"/> — deliberately <b>not</b> under
/// <see cref="SaleSourceSystems.VanSales"/>. This sale is already represented by the confirmed
/// <c>StockReservation</c> the invoice posted from, so an offline-van row for it would be counted twice by
/// <c>VanSalesFactReader</c> and posted a second time by <c>VanSalesEndOfDayPostingService</c>. The row
/// written here carries the receipt and nothing else: it arrives already
/// <see cref="DesktopSaleConsolidationStatus.Consolidated"/>, with the posted document on it, so no
/// posting route can claim it.
/// </para>
/// </remarks>
public sealed class CreateVanSalesDirectInvoiceHandler(
    ApplicationDbContext db,
    IMediator mediator,
    IOptions<FiscalisationSettings> fiscalisationOptions,
    ILogger<CreateVanSalesDirectInvoiceHandler> logger
) : IRequestHandler<CreateVanSalesDirectInvoiceCommand, ErrorOr<VanSalesDirectInvoiceResponse>>
{
    public async Task<ErrorOr<VanSalesDirectInvoiceResponse>> Handle(
        CreateVanSalesDirectInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(command.Request.Type) &&
            !string.Equals(command.Request.Type, "INV", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidOrderType",
                "Only invoice payloads are supported by the direct van sales invoice endpoint.");
        }

        // Before anything reaches SAP, and that ordering is the whole point of putting it here.
        //
        // The switch says an unstamped van sale may not be accepted. Checking it after the post would
        // "refuse" a sale that already exists in SAP as a real A/R invoice — the handset would be told no,
        // would keep the sale, and the invoice would sit there with nothing pointing at it.
        if (fiscalisationOptions.Value.RequireStampedVanSales && !command.Request.ClaimsReceiptSequence())
        {
            logger.LogError(
                "Van sale {Reference} was refused before posting: it carries no fiscal receipt and " +
                "Fiscalisation:RequireStampedVanSales is on. This handset is on a build older than the " +
                "signing release and cannot trade until it is updated.",
                command.Request.VanOrder);

            return Error.Validation(
                "VanSalesCompatibility.UnstampedSale",
                "This sale carries no fiscal receipt. Stamped receipts are now required, so it cannot be " +
                "accepted — update the handset to a build that signs receipts.");
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

        var customer = await ResolveCustomerAsync(
            command.Request,
            user,
            cancellationToken);
        if (customer is null)
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidCustomer",
                "The selected customer is not assigned to the current user.");
        }

        var invoiceRequest = VanSalesCompatibilityMapper.MapInvoiceRequest(
            command.Request,
            customer,
            warehouseCode,
            costCentreCode);

        var result = await mediator.Send(
            new CreateInvoiceDirectCommand(invoiceRequest, command.UserId.ToString()),
            cancellationToken);

        if (result.IsError)
        {
            return result.Errors;
        }

        // No cancellationToken past this line, and that is the point of where the line is. The money is
        // in SAP; everything after it is a durable obligation the caller no longer governs.
        await PersistSignedReceiptAsync(
            command.Request,
            result.Value,
            customer,
            command.UserId,
            warehouseCode,
            costCentreCode);

        return VanSalesCompatibilityMapper.MapInvoiceResponse(
            result.Value, command.Request.VanOrder, command.Request);
    }

    /// <summary>
    /// Stores the receipt the handset signed, so the drain can hand it to the fiscalisation platform.
    /// </summary>
    /// <remarks>
    /// <b>After the post, and it can never fail the sale.</b> Both halves of that are deliberate.
    ///
    /// <para>
    /// After, because until SAP has accepted the invoice there is no sale to attach a receipt to, and a
    /// row written before a post that then failed would claim a fiscal document for a sale that does not
    /// exist. It also has to be after because the posted document number belongs on the row: that is what
    /// ties the ZIMRA receipt to the SAP invoice for anyone reconciling the two.
    /// </para>
    ///
    /// <para>
    /// And it cannot fail the sale, because by the time it runs the money has reached SAP. Throwing here
    /// would tell the handset the sale failed, and a handset told that re-sends — against an invoice that
    /// already exists, which the reservation's own idempotency then has to catch. A receipt that could not
    /// be written is a serious incident and is logged as one; it is not a reason to lie to the rep about
    /// where the customer's money went.
    /// </para>
    ///
    /// <para>
    /// It writes through the same request-scoped context <c>StockReservationService</c> used, whose own
    /// work is committed by the time control returns here. A failure is therefore isolated by detaching
    /// what this method added, so a half-built row cannot ride along on somebody else's later save.
    /// </para>
    ///
    /// <para>
    /// <b>And it never takes the request's token.</b> ASP.NET binds that to
    /// <c>HttpContext.RequestAborted</c>, so passing it here would make the one thing that must happen
    /// after the post the one thing a disconnect cancels. The vans on this path sell at the edge of
    /// coverage: a handset that drops the connection while waiting for the reply is the ordinary case,
    /// not the exotic one, and it arrives precisely in the window between SAP accepting the invoice and
    /// this row being written. The result would be the exact loss this method exists to prevent, and it
    /// would look like a clean cancellation in the logs. Same rule, same reason, as the last safe abort
    /// in <c>ConsolidateDailySalesHandler</c> and <c>CreateTransferRequestHandler</c>: past the commit
    /// point the caller going away must not be able to stop the record of what was committed.
    /// </para>
    /// </remarks>
    private async Task PersistSignedReceiptAsync(
        VanSalesOrderRequest request,
        ConfirmReservationResponseDto posted,
        VanSalesCustomerResolution customer,
        Guid userId,
        string warehouseCode,
        string costCentreCode)
    {
        // Deliberately and literally CancellationToken.None — see the remarks above. Held in a local so
        // that a future edit adding a read here cannot quietly reintroduce the request token.
        var persist = CancellationToken.None;

        var reference = request.VanOrder?.Trim();

        if (string.IsNullOrWhiteSpace(reference))
        {
            // Nothing to key the row on, and ExternalReferenceId is the unique index. A sale with no
            // van_order predates the fiscal contract entirely and carries no receipt to store.
            return;
        }

        DesktopSaleEntity? sale = null;

        try
        {
            // A handset that lost the reply re-sends, and the reservation answers the second request with
            // the invoice the first one posted. The receipt is already stored from that first attempt, and
            // the unique index on ExternalReferenceId would turn the duplicate into a failed save.
            var alreadyStored = await db.DesktopSales
                .AsNoTracking()
                .AnyAsync(existing => existing.ExternalReferenceId == reference, persist);

            if (alreadyStored)
            {
                return;
            }

            sale = BuildReceiptRow(request, posted, customer, userId, warehouseCode, costCentreCode, reference);

            // Before the insert, not after it. The row is subject to CK_DesktopSaleLines_Quantity_Positive
            // and the non-negative money constraints, and a constraint violation arrives as an opaque
            // provider exception naming a constraint rather than the field that was wrong — a poor thing
            // to be told about a receipt that is now unrecoverable. The offline handler validates the same
            // shape up front (IngestVanSalesOfflineSalesHandler.Validate); this path had nothing.
            var invalid = DescribeUnstorableRow(sale);

            if (invalid is not null)
            {
                logger.LogError(
                    "Online van sale {Reference} posted to SAP as {SapDocNum} but its signed ZIMRA receipt " +
                    "cannot be stored: {Reason} The sale stands and the customer holds the printed " +
                    "receipt, but the receipt on that handset is now one this server cannot account for — " +
                    "nothing will hand it to the fiscalisation platform, and device {FiscalDeviceId} " +
                    "fiscal day {FiscalDayNo} will close short of receipt {ReceiptGlobalNo}. FDMS " +
                    "reconciles a day against a contiguous chain. This needs a person.",
                    reference,
                    posted.SAPDocNum,
                    invalid,
                    request.FiscalDeviceId,
                    request.FiscalDayNo,
                    request.ReceiptGlobalNo);

                await RaiseReceiptStorageIncidentAsync(reference, request, posted, invalid, persist);
                return;
            }

            db.DesktopSales.Add(sale);
            await db.SaveChangesAsync(persist);

            if (sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unstamped)
            {
                logger.LogWarning(
                    "Online van sale {Reference} on device {FiscalDeviceId} was never stamped — the handset " +
                    "is on a build older than the signing release, so the server fiscalised the invoice on " +
                    "a device that is not this van's. Nothing is blocked and the sale is safe, but the " +
                    "receipt is on the wrong chain. Update this handset, then turn on " +
                    "Fiscalisation:RequireStampedVanSales once the fleet is done.",
                    reference,
                    request.FiscalDeviceId);
            }
            else if (sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Unsignable)
            {
                logger.LogError(
                    "Online van sale {Reference} arrived without a usable device signature, so its ZIMRA " +
                    "receipt cannot be submitted. Receipt {ReceiptGlobalNo}/{ReceiptCounter} on device " +
                    "{FiscalDeviceId}, fiscal day {FiscalDayNo}. Its number is spent, so every later receipt " +
                    "from this handset is blocked behind it. SAP invoice {SapDocNum} is posted and correct; " +
                    "the fiscal side needs a person.",
                    reference,
                    request.ReceiptGlobalNo,
                    request.ReceiptCounter,
                    request.FiscalDeviceId,
                    request.FiscalDayNo,
                    posted.SAPDocNum);
            }
        }
        catch (Exception ex)
        {
            if (sale is not null)
            {
                // Otherwise the failed insert stays tracked as Added and the next SaveChanges on this
                // request's context — anyone's — retries it and fails for the same reason. The lines go
                // with it: detaching a principal does not detach its dependents, and a set of orphaned
                // Added lines would fail the very SaveChanges that writes the incident below.
                //
                // Over a copy, because detaching a line makes EF fix up the navigation and remove it from
                // sale.Lines, which is the collection being walked.
                foreach (var line in sale.Lines.ToList())
                {
                    db.Entry(line).State = EntityState.Detached;
                }

                db.Entry(sale).State = EntityState.Detached;
            }

            logger.LogError(
                ex,
                "Online van sale {Reference} posted to SAP as {SapDocNum} but its signed ZIMRA receipt " +
                "could not be stored. The sale stands and the customer holds the printed receipt, but " +
                "the receipt on that handset is now one this server cannot account for: nothing will hand " +
                "it to the fiscalisation platform, device {FiscalDeviceId} fiscal day {FiscalDayNo} will " +
                "close short of receipt {ReceiptGlobalNo}, and FDMS reconciles a day against a contiguous " +
                "chain. Worse, the shortfall is invisible to the check that would catch it — with no row " +
                "on this table the fiscal day lifecycle counts this device-day as settled, closes it and " +
                "uploads it, and FDMS refuses the day. This needs a person.",
                reference,
                posted.SAPDocNum,
                request.FiscalDeviceId,
                request.FiscalDayNo,
                request.ReceiptGlobalNo);

            await RaiseReceiptStorageIncidentAsync(
                reference, request, posted, $"The receipt row could not be written: {ex.Message}", persist);
        }
    }

    /// <summary>
    /// Why this row cannot be stored, or null if it can.
    /// </summary>
    /// <remarks>
    /// Every rule here is one the database would otherwise enforce as a check constraint or a NOT NULL,
    /// stated in the terms of the payload that broke it. Discovering a bad quantity as
    /// <c>CK_DesktopSaleLines_Quantity_Positive</c>, on a receipt that is already printed and already
    /// spent a number, tells whoever reads the incident nothing about which line to look at.
    ///
    /// <para>
    /// The empty-cart case is not a constraint but belongs with them: the platform refuses a receipt with
    /// no lines outright, so a row carrying none could never be handed over even if it stored cleanly.
    /// </para>
    /// </remarks>
    private static string? DescribeUnstorableRow(DesktopSaleEntity sale)
    {
        if (sale.Lines.Count == 0)
        {
            return "the sale carries no line items, and a receipt with no lines is one the platform refuses.";
        }

        foreach (var line in sale.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.ItemCode))
            {
                return $"line {line.LineNum} has no item code.";
            }

            if (line.Quantity <= 0m)
            {
                return $"line {line.LineNum} ({line.ItemCode}) has a quantity of {line.Quantity}, which must be above zero.";
            }

            if (line.UnitPrice < 0m || line.LineTotal < 0m)
            {
                return $"line {line.LineNum} ({line.ItemCode}) is priced negatively at {line.UnitPrice} a unit.";
            }
        }

        if (sale.TotalAmount < 0m || sale.VatAmount < 0m || sale.AmountPaid < 0m)
        {
            return $"the sale totals are negative: {sale.TotalAmount} total, {sale.VatAmount} VAT, {sale.AmountPaid} paid.";
        }

        return null;
    }

    /// <summary>
    /// Puts a lost receipt in front of a person, because a log line is not a control.
    /// </summary>
    /// <remarks>
    /// The log already says what happened, and on its own that is where this ends: nothing polls the logs
    /// and nothing else on this server will ever notice. What makes it urgent rather than untidy is that
    /// the loss also disables its own detector. <c>FiscalDayLifecycleService.CountOutstandingReceiptsAsync</c>
    /// counts a device-day's outstanding receipts by reading this table, so a receipt with no row here is
    /// not outstanding — it is absent. The day therefore looks settled, is auto-closed, packaged and
    /// uploaded, and FDMS refuses it for a gap nobody on this side can see.
    ///
    /// <para>
    /// Raised the way <c>CreditNoteService</c> and <c>PaymentGatewayService</c> raise theirs, as an
    /// <c>ExceptionCenterIncidentEntity</c>, so it reaches the Exception Center where fiscal failures are
    /// already worked. <c>CanRetry</c> is false: there is no button that can re-obtain a receipt that
    /// exists only on a handset, and offering one would suggest otherwise. The way back is to read the
    /// receipt off the device.
    /// </para>
    ///
    /// <para>
    /// Its own try/catch, and only a warning if it fails. It is raised on paths that are already handling
    /// a failure — one of them a failed <c>SaveChanges</c> on this very context — so it is exactly the
    /// call most likely to fail again, and throwing would replace a logged loss with an exception thrown
    /// at a handset whose sale succeeded.
    /// </para>
    /// </remarks>
    private async Task RaiseReceiptStorageIncidentAsync(
        string reference,
        VanSalesOrderRequest request,
        ConfirmReservationResponseDto posted,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTime.UtcNow;

            var incident = new ExceptionCenterIncidentEntity
            {
                Source = ExceptionCenterSources.VanSaleReceiptStorage,
                Category = "Fiscalisation",
                Title = "Signed van receipt could not be stored",
                Reference = Truncate(reference, 200),
                Status = "RequiresReview",
                SourceSystem = Truncate(
                    $"Device {request.FiscalDeviceId}, fiscal day {request.FiscalDayNo}", 50),
                Provider = "Fiscalisation",
                LastError = Truncate(
                    $"{reason} SAP invoice {posted.SAPDocNum} is posted and correct, so the money is safe " +
                    $"and the customer holds a printed ZIMRA receipt — but this server has no record of " +
                    $"that receipt and cannot hand it to the fiscalisation platform. Receipt " +
                    $"{request.ReceiptGlobalNo}/{request.ReceiptCounter} on device {request.FiscalDeviceId}, " +
                    $"fiscal day {request.FiscalDayNo}. Because no row exists, the fiscal day lifecycle " +
                    $"reads this device-day as settled and will close and upload it; FDMS then refuses the " +
                    $"day for a gap this server cannot see. Recover the receipt from the handset.",
                    2000),
                RetryCount = 0,
                MaxRetries = 0,
                CanRetry = false,
                CreatedAtUtc = now,
                OccurredAtUtc = now,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    VanOrder = reference,
                    SapDocEntry = posted.SAPDocEntry,
                    SapDocNum = posted.SAPDocNum,
                    request.FiscalDeviceId,
                    request.FiscalDayNo,
                    request.ReceiptGlobalNo,
                    request.ReceiptCounter,
                    request.VerificationCode,
                    Reason = reason
                })
            };

            db.ExceptionCenterIncidents.Add(incident);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to raise the lost-receipt incident for online van sale {Reference}. The loss is " +
                "recorded in the error above it and nowhere else.",
                reference);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// The row that carries the receipt. Everything on it that is not the receipt exists to keep some
    /// other route from mistaking it for work.
    /// </summary>
    private static DesktopSaleEntity BuildReceiptRow(
        VanSalesOrderRequest request,
        ConfirmReservationResponseDto posted,
        VanSalesCustomerResolution customer,
        Guid userId,
        string warehouseCode,
        string costCentreCode,
        string reference)
    {
        var lines = request.Items.Select((item, index) =>
        {
            // The tax-inclusive unit price the receipt was signed over. Rounded here only to produce the
            // line total, exactly as the handset's composer does — the price itself is stored as sent.
            var unitPrice = Convert.ToDecimal(item.Price, System.Globalization.CultureInfo.InvariantCulture);

            return new DesktopSaleLineEntity
            {
                LineNum = index,
                ItemCode = item.Code.Trim(),
                ItemDescription = item.Description,
                Quantity = item.Quantity,
                UnitPrice = unitPrice,
                LineTotal = Math.Round(unitPrice * item.Quantity, 2, MidpointRounding.AwayFromZero),
                WarehouseCode = warehouseCode,

                // Carried so the signed receipt can be rebuilt for the platform. Order matters as much as
                // the values do — the receipt was signed over these lines in the order they arrived.
                TaxCode = item.TaxCode,
                TaxId = item.TaxId,
                TaxPercent = item.TaxPercent,
                HsCode = item.HsCode
            };
        }).ToList();

        var total = lines.Sum(line => line.LineTotal);

        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSalesOnline,
            CardCode = customer.PostingCardCode,
            CardName = request.Reference,
            RouteCustomerId = customer.RouteCustomerId,
            RouteCustomerCode = customer.RouteCustomerCode,
            RouteCustomerName = customer.RouteCustomerName,

            // The trading day the invoice posted against, so the two records agree about which day this
            // sale belongs to even though only the reservation is counted for it.
            DocDate = (VanSalesCompatibilityMapper.ParseLegacyDate(request.DueDate) ?? DateTime.UtcNow).Date,
            NumAtCard = reference,

            // Derived from the lines the same way the handset's composer derives the printed total, which
            // is the same way the platform will derive it again: Σ round(price × qty). The receipt is
            // signed over the lines, not over a total, so a total sent separately would be a second
            // opinion about a number that has exactly one right answer.
            TotalAmount = total,
            VatAmount = lines.Sum(TaxInclusivePortion),
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim(),

            // SAP already holds the invoice. Consolidated with the document on it is what stops every
            // posting route — the 18:00 consolidation, the van mop-up, the desktop posting job — from
            // treating this row as a sale still owed to SAP and invoicing it a second time.
            //
            // Consolidated even in the one case where SAP does not hold it yet: when the circuit was open
            // the reservation was queued instead, and the queue will post it. Either way the document is
            // the reservation's to produce and never this row's, and marking it Pending would offer a
            // duplicate invoice to whichever job noticed first.
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            SapDocEntry = posted.SAPDocEntry,
            SapDocNum = posted.SAPDocNum,
            PostedAt = posted.SAPDocNum.HasValue ? DateTime.UtcNow : null,

            WarehouseCode = warehouseCode,
            CostCentreCode = costCentreCode,
            PaymentMethod = string.IsNullOrWhiteSpace(request.PaymentMethod)
                ? null
                : request.PaymentMethod.Trim(),
            // Settled, not tendered. `amount_paid` on this DTO sits next to `change`, so it is what the
            // customer handed over — 100.00 against a 92.50 sale when they paid with a note. AmountPaid
            // on this table means money the business kept: the offline van DTO has no `change` field and
            // sends exactly that, and every reader that sums this column is reconciling cash. Storing the
            // tender here would inflate that sum by the change given, silently and only on this one
            // source. Floored at zero because the column is check-constrained non-negative and a handset
            // that reported change larger than the tender is describing something else entirely.
            AmountPaid = Math.Max(
                0m,
                Convert.ToDecimal(request.AmountPaid, System.Globalization.CultureInfo.InvariantCulture)
                - Convert.ToDecimal(request.Change, System.Globalization.CultureInfo.InvariantCulture)),
            CreatedBy = userId.ToString(),
            CreatedAt = DateTime.UtcNow,
            Lines = lines
        };

        sale.ApplySignedReceipt(
            request,
            "The handset stamped no fiscal receipt for this sale, so the server fiscalised the SAP invoice " +
            "instead — on a device that is not this van's. The customer has a receipt and ZIMRA has a " +
            "record, but not on this handset's chain. Update the handset.");

        return sale;
    }

    /// <summary>
    /// The tax inside a tax-inclusive line total, for reporting only.
    /// </summary>
    /// <remarks>
    /// Not part of the signature and never sent to the platform, which derives the whole tax block itself
    /// from the lines and takes nobody's word for it. It is here so the row's VAT column means something
    /// to a person reading it, and it uses the same tax-inclusive formula the platform uses so the two
    /// figures do not disagree on screen.
    /// </remarks>
    private static decimal TaxInclusivePortion(DesktopSaleLineEntity line) =>
        line.TaxPercent is > 0m
            ? Math.Round(
                line.LineTotal * line.TaxPercent.Value / (100m + line.TaxPercent.Value),
                2,
                MidpointRounding.AwayFromZero)
            : 0m;

    private async Task<VanSalesCustomerResolution?> ResolveCustomerAsync(
        VanSalesOrderRequest request,
        Models.User user,
        CancellationToken cancellationToken)
    {
        if (VanSalesRouteCustomerScope.UsesLocalRouteCustomers(user))
        {
            var routeCustomers = await VanSalesRouteCustomerScope.GetAssignedRouteCustomersAsync(db, user, cancellationToken);
            var selectedCustomer = routeCustomers.FirstOrDefault(
                customer => VanSalesCompatibilityMapper.MatchesRequestedCustomer(request, customer.Code));

            var postingCardCode = user.AssignedBusinessPartnerCode?.Trim();
            return selectedCustomer is null || string.IsNullOrWhiteSpace(postingCardCode)
                ? null
                : new VanSalesCustomerResolution(postingCardCode, selectedCustomer);
        }

        var effectiveCustomerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
            db,
            user,
            logger,
            cancellationToken);

        var normalizedCodes = effectiveCustomerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(request.CustomerCode))
        {
            var requestedCode = request.CustomerCode.Trim();
            return normalizedCodes.Contains(requestedCode, StringComparer.OrdinalIgnoreCase)
                ? new VanSalesCustomerResolution(requestedCode, null)
                : null;
        }

        var encodedMatch = normalizedCodes.FirstOrDefault(
            code => VanSalesCompatibilityMapper.EncodeCompatibilityId(code) == request.Customer);

        return encodedMatch is null ? null : new VanSalesCustomerResolution(encodedMatch, null);
    }
}
