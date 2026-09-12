using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Common.Security;
using ShopInventory.Features.DesktopCreditNotes;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/DesktopIntegration/sales/{reference}/credit-notes")]
[Authorize(Policy = "ApiAccess", Roles = "Admin,Cashier,Manager,ApiUser")]
public sealed class DesktopCreditNotesController(DesktopCreditNoteService service,
    ILogger<DesktopCreditNotesController> logger) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> List(string reference, CancellationToken ct) =>
        Run(id => service.ListAsync(id, reference, ct));

    [HttpGet("prepare")]
    public Task<IActionResult> Prepare(string reference, CancellationToken ct) =>
        Run(id => service.PrepareAsync(id, reference, ct));

    [HttpPost]
    public Task<IActionResult> Create(string reference, CreateDesktopCreditRequest request, CancellationToken ct) =>
        Run(id => service.CreateAsync(id, reference, request, ct));

    [HttpPost("{id:guid}/reconcile")]
    public Task<IActionResult> Reconcile(string reference, Guid id, CancellationToken ct) =>
        Run(caller => service.ReconcileAsync(caller, reference, id, ct));

    [HttpPost("{id:guid}/continue")]
    public Task<IActionResult> Continue(string reference, Guid id, CancellationToken ct) =>
        Run(caller => service.ContinueAsync(caller, reference, id, ct));

    private async Task<IActionResult> Run<T>(Func<Guid, Task<T>> action)
    {
        var id = UserClaimReader.GetUserId(User);
        if (!id.HasValue) return Unauthorized();
        try { return Ok(await action(id.Value)); }
        catch (UnauthorizedAccessException ex) { return Problem(statusCode: 403, detail: ex.Message); }
        catch (InvalidOperationException ex) { return Problem(statusCode: 409, detail: ex.Message); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Desktop credit-note operation did not complete");
            return Problem(statusCode: 503, detail: "The credit-note status could not be confirmed. Reload the saved notes before trying again.");
        }
    }
}
