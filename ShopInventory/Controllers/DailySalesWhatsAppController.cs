using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.DailySalesWhatsApp.Commands.SendDailySalesWhatsApp;
using ShopInventory.Features.DailySalesWhatsApp.Commands.UpdateDailySalesWhatsAppSettings;
using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppReport;
using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// The daily sales report on WhatsApp: yesterday's van sales by route and one shop's product quantities,
/// sent each morning to the numbers an admin saves in Settings.
/// </summary>
[Route("api/daily-sales-whatsapp")]
[Authorize(Roles = "Admin")]
[Produces("application/json")]
public sealed class DailySalesWhatsAppController(ISender mediator) : ApiControllerBase
{
    /// <summary>Whether the report is on, who gets it, which shop it reads, the rows of both tables, and what it last sent.</summary>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(DailySalesWhatsAppSettings), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetDailySalesWhatsAppSettingsQuery(), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>Saves the report's switch, numbers, shop and rows.</summary>
    [HttpPut("settings")]
    [ProducesResponseType(typeof(DailySalesWhatsAppSettings), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] UpdateDailySalesWhatsAppSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new UpdateDailySalesWhatsAppSettingsCommand(
                User.Identity?.Name,
                request.Enabled,
                request.Recipients ?? [],
                request.ShopWarehouseCode,
                request.ShopHeading,
                request.VanRows ?? [],
                request.ProductRows ?? []),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>The report for one day as it would be sent — figures and message text — without sending it. Yesterday when no date is given.</summary>
    [HttpGet("preview")]
    [ProducesResponseType(typeof(DailySalesWhatsAppReport), StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview([FromQuery] DateTime? date, CancellationToken cancellationToken)
    {
        var day = date?.Date ?? AuditService.ToCAT(DateTime.UtcNow).Date.AddDays(-1);
        var result = await mediator.Send(new GetDailySalesWhatsAppReportQuery(day), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>Sends the report now — for the day and numbers given, or yesterday to the saved numbers.</summary>
    [HttpPost("send")]
    [ProducesResponseType(typeof(DailySalesWhatsAppSendResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Send(
        [FromBody] SendDailySalesWhatsAppRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new SendDailySalesWhatsAppCommand(false, request.TradingDate, request.Recipients, User.Identity?.Name),
            cancellationToken);

        return result.Match(Ok, Problem);
    }
}
