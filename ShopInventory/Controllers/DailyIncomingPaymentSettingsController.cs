using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// The switch between desktop sales posting invoices only and invoices plus the daily incoming payment.
/// </summary>
[Route("api/daily-incoming-payment-settings")]
[Authorize(Policy = "AdminOnly")]
public class DailyIncomingPaymentSettingsController(
    DailyIncomingPaymentSwitch paymentSwitch,
    IAuditService auditService,
    ILogger<DailyIncomingPaymentSettingsController> logger) : ApiControllerBase
{
    /// <summary>
    /// Whether the daily incoming payment for desktop sales is on
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await paymentSwitch.GetAsync(cancellationToken));

    /// <summary>
    /// Turn the daily incoming payment for desktop sales on or off; takes effect on the job's next run
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> Update(
        [FromBody] UpdateDailyIncomingPaymentSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var userName = User.Identity?.Name ?? "Unknown";
        var state = await paymentSwitch.SetAsync(request.Enabled, cancellationToken);

        logger.LogWarning(
            "Daily incoming payments for desktop sales switched {State} by {User}",
            request.Enabled ? "ON" : "OFF", userName);

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateDailyIncomingPaymentSettings,
                "DailyIncomingPayment",
                null,
                $"Daily incoming payments for desktop sales switched {(request.Enabled ? "on" : "off")} by {userName}",
                true);
        }
        catch
        {
        }

        return Ok(state);
    }
}

public sealed class UpdateDailyIncomingPaymentSettingsRequest
{
    public bool Enabled { get; set; }
}
