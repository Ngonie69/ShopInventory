using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalDayStates;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalisationConsoleDevices;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalisationWorkQueue;

namespace ShopInventory.Controllers;

/// <summary>
/// Everything the fiscalisation console reads: the devices, what is still owed to ZIMRA, and how far
/// each fiscal day has got.
/// </summary>
/// <remarks>
/// Read-only, deliberately and completely. Every fiscal action worth having on one screen — closing a
/// day, generating a file, submitting it, resending a receipt — is irreversible at FDMS, and several of
/// them are not idempotent there. So this controller answers questions and the existing, individually
/// guarded routes take the actions: the console retries an invoice through
/// <c>POST /api/invoice/{docEntry}/fiscalize</c>, which already refuses a consolidated invoice and one
/// whose sale was fiscalised before it reached SAP.
///
/// Nothing here writes, so nothing here needs an idempotency key, and a nervous operator refreshing the
/// page cannot make anything worse.
/// </remarks>
[Route("api/fiscalisation-console")]
[Authorize(Policy = "AdminOnly")]
public class FiscalisationConsoleController(IMediator mediator) : ApiControllerBase
{
    /// <summary>Every fiscal device, what ZIMRA says about it, and what is queued against it here.</summary>
    [HttpGet("devices")]
    public async Task<IActionResult> GetDevices(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetFiscalisationConsoleDevicesQuery(), cancellationToken);
        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }

    /// <summary>
    /// Documents eligible for or failed at fiscalisation, narrowed in the database.
    /// </summary>
    /// <remarks>
    /// Every parameter reaches SQL. That is the whole difference between this and the fiscal status
    /// filter on the invoices page, which narrows a page that has already been fetched and so can never
    /// answer "how many are outstanding".
    /// </remarks>
    [HttpGet("work-queue")]
    public async Task<IActionResult> GetWorkQueue(
        [FromQuery] string? status,
        [FromQuery] int? deviceId,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetFiscalisationWorkQueueQuery(status, deviceId, fromDate, toDate, search, page, pageSize),
            cancellationToken);

        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }

    /// <summary>One row per device per fiscal day, and how far it got towards ZIMRA.</summary>
    [HttpGet("fiscal-days")]
    public async Task<IActionResult> GetFiscalDays(
        [FromQuery] int? deviceId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetFiscalDayStatesQuery(deviceId, status, page, pageSize),
            cancellationToken);

        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }
}
