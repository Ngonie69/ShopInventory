using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.FiscalisationConfiguration.Commands.TestFiscalisationConnection;
using ShopInventory.Features.FiscalisationConfiguration.Commands.UpdateFiscalisationSettings;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalisationSettings;
using ShopInventory.Features.FiscalPrintForms.Commands.DeleteFiscalPrintForm;
using ShopInventory.Features.FiscalPrintForms.Commands.SaveFiscalPrintForm;
using ShopInventory.Features.FiscalPrintForms.Queries.GetFiscalPrintForms;

namespace ShopInventory.Controllers;

[Route("api/fiscalisation-settings")]
[Authorize(Policy = "AdminOnly")]
public class FiscalisationSettingsController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Current fiscalisation settings; the API key comes back masked
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetFiscalisationSettingsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Store a new API key
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> UpdateSettings(
        [FromBody] UpdateFiscalisationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var userName = User.Identity?.Name ?? "Unknown";
        var result = await mediator.Send(
            new UpdateFiscalisationSettingsCommand(request, userName), cancellationToken);

        return result.Match(
            value => Ok(new
            {
                message = value.Message,
                connectionTestPassed = value.ConnectionTestPassed,
                apiKeyMasked = value.ApiKeyMasked
            }),
            errors => Problem(errors));
    }

    /// <summary>
    /// Check a key against the platform
    /// </summary>
    [HttpPost("test-connection")]
    public async Task<IActionResult> TestConnection(
        [FromBody] TestFiscalisationConnectionRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new TestFiscalisationConnectionCommand(request), cancellationToken);
        return result.Match(
            value => Ok(new { connected = value.Connected, message = value.Message }),
            errors => Problem(errors));
    }

    /// <summary>
    /// The business partners whose till, vending and van sales are fiscalised as a chosen document type.
    /// Every other partner, and every other channel, is an A4 invoice.
    /// </summary>
    [HttpGet("print-forms")]
    public async Task<IActionResult> GetPrintForms(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetFiscalPrintFormsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Set a business partner's document type: Receipt48 or InvoiceA4
    /// </summary>
    [HttpPut("print-forms/{cardCode}")]
    public async Task<IActionResult> SavePrintForm(
        string cardCode,
        [FromBody] SaveFiscalPrintFormRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new SaveFiscalPrintFormCommand(cardCode, request.CardName, request.PrintForm, User.Identity?.Name),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Return a business partner to the A4 invoice default
    /// </summary>
    [HttpDelete("print-forms/{cardCode}")]
    public async Task<IActionResult> DeletePrintForm(string cardCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new DeleteFiscalPrintFormCommand(cardCode, User.Identity?.Name), cancellationToken);

        return result.Match(_ => NoContent(), errors => Problem(errors));
    }
}
