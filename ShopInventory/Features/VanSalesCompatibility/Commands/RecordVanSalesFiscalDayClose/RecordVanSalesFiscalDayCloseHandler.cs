using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.RecordVanSalesFiscalDayClose;

/// <summary>
/// Holds a handset's signed fiscal day close until the day it closes is packaged and uploaded.
///
/// <para>Why it is stored rather than acted on immediately: the handset signs its close the moment its
/// day ends, which is out on a route, possibly with no signal and certainly before its last receipts have
/// reached this service. The day cannot be packaged until they have. So the close waits here, and
/// <c>FiscalDayLifecycleService</c> picks it up when the day is actually ready to go.</para>
///
/// <para><b>Nothing is normalised, recomputed or repaired.</b> The handset's signature covers the exact
/// counter values it sent, and the platform recomputes the totals from the receipts it archived and
/// refuses a close that disagrees. Re-casing a currency or filling in an absent tax percentage on the way
/// through would turn a good close into a refused one. What arrives is what is stored and what is
/// forwarded.</para>
///
/// <para>The signature is not checked here either, and deliberately: this service holds no certificate.
/// The platform verifies it against the device's own, which is the only place that check means anything.
/// Rejecting a close here on a guess would strand a day this service cannot close by any other route.</para>
/// </summary>
public sealed class RecordVanSalesFiscalDayCloseHandler(
    ApplicationDbContext context,
    ILogger<RecordVanSalesFiscalDayCloseHandler> logger)
    : IRequestHandler<RecordVanSalesFiscalDayCloseCommand, ErrorOr<VanSalesFiscalDayCloseResponse>>
{
    public async Task<ErrorOr<VanSalesFiscalDayCloseResponse>> Handle(
        RecordVanSalesFiscalDayCloseCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        if (request.device_id <= 0 || request.fiscal_day_no <= 0)
        {
            return Error.Validation(
                "VanSalesCompatibility.FiscalDayCloseIncomplete",
                "A fiscal day close must name the device and the day it closes.");
        }

        if (string.IsNullOrWhiteSpace(request.signature_value))
        {
            return Error.Validation(
                "VanSalesCompatibility.FiscalDayCloseUnsigned",
                "A fiscal day close must carry the device signature over its counters. The platform holds " +
                "no key for this device and cannot sign one on its behalf.");
        }

        // An empty declaration is not the same as no declaration: it asserts the day sold nothing. A
        // handset that failed to load its receipts would send exactly that, and the platform would refuse
        // the day for totals that disagree with the receipts it holds.
        if (request.counters.Count == 0)
        {
            return Error.Validation(
                "VanSalesCompatibility.FiscalDayCloseEmpty",
                "A fiscal day close must declare the day's counters. An empty declaration asserts the day " +
                "traded nothing.");
        }

        var state = await context.FiscalDayStates
            .FirstOrDefaultAsync(
                row => row.DeviceId == request.device_id && row.FiscalDayNo == request.fiscal_day_no,
                cancellationToken);

        if (state is null)
        {
            // The lifecycle creates the row when it first sees receipts for a day. A close arriving before
            // any receipt has is out of order rather than wrong, and the handset should re-send once its
            // backlog has drained.
            return Error.NotFound(
                "VanSalesCompatibility.FiscalDayNotKnown",
                $"Fiscal day {request.fiscal_day_no} is not yet known for device {request.device_id}. " +
                "Upload the day's receipts before closing it.");
        }

        if (!string.IsNullOrWhiteSpace(state.DeclaredCloseJson))
        {
            // Re-arrival is routine: a handset that loses the response re-sends. Answering duplicate lets
            // it clear its queue instead of retrying forever, and the held close is left exactly as it is
            // -- overwriting it after the day was packaged would replace a close already in flight.
            logger.LogInformation(
                "Device {DeviceId} re-sent the signed close for fiscal day {FiscalDayNo}; the held one is kept.",
                request.device_id,
                request.fiscal_day_no);

            return new VanSalesFiscalDayCloseResponse
            {
                accepted = true,
                duplicate = true,
                device_id = request.device_id,
                fiscal_day_no = request.fiscal_day_no,
                message = "This day's close was already held."
            };
        }

        state.DeclaredCloseJson = JsonSerializer.Serialize(ToPlatformShape(request));
        state.DeclaredCloseReceivedAtUtc = DateTime.UtcNow;
        state.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Held a device-signed close for device {DeviceId}, fiscal day {FiscalDayNo}: {CounterCount} counter(s). " +
            "It travels with the day's last offline file.",
            request.device_id,
            request.fiscal_day_no,
            request.counters.Count);

        return new VanSalesFiscalDayCloseResponse
        {
            accepted = true,
            duplicate = false,
            device_id = request.device_id,
            fiscal_day_no = request.fiscal_day_no
        };
    }

    /// <summary>
    /// Maps the handset's snake-cased payload onto the platform's request shape, once, on arrival.
    /// </summary>
    /// <remarks>
    /// Done here rather than at forwarding time so the stored value is already the exact JSON the platform
    /// will receive. Mapping later would mean the shape crossing this service twice, and each crossing is
    /// an opportunity to alter a value the signature covers.
    /// </remarks>
    private static DeclaredFiscalDayCloseApiRequest ToPlatformShape(VanSalesFiscalDayCloseRequest request) => new()
    {
        SignatureHash = request.signature_hash,
        SignatureValue = request.signature_value,
        Counters = [.. request.counters.Select(counter => new DeclaredFiscalDayCounterApiRequest
        {
            FiscalCounterType = counter.counter_type,
            FiscalCounterCurrency = counter.currency,
            FiscalCounterTaxID = counter.tax_id,
            FiscalCounterTaxPercent = counter.tax_percent,
            FiscalCounterMoneyType = counter.money_type,
            FiscalCounterValue = counter.value
        })]
    };
}
