using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Common.Security;
using ShopInventory.Features.DesktopCreditNotes;

namespace ShopInventory.Controllers;

/// <summary>
/// Every desktop credit across all sales. <see cref="DesktopCreditNotesController"/> is the same
/// credits one sale at a time, which is where they are raised; this is where they are chased.
/// </summary>
/// <remarks>
/// Read with the same roles as a single sale's credits, and confined the same way: a shop-bound
/// account sees its own warehouse only, whatever it asks for.
/// </remarks>
[ApiController]
[Route("api/DesktopIntegration/credit-notes")]
[Authorize(Policy = "ApiAccess", Roles = "Admin,Cashier,Manager,ApiUser,CartVendor")]
public sealed class DesktopCreditNoteListController(
    DesktopCreditNoteListService service,
    ILogger<DesktopCreditNoteListController> logger) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> List([FromQuery] DesktopCreditNoteListQuery query, CancellationToken ct) =>
        Run(id => service.ListAsync(id, query, ct));

    /// <summary>Sends a refused SAP credit memo again. It raises a SAP document, so managers only.</summary>
    [HttpPost("{id:guid}/retry-sap")]
    [Authorize(Policy = "ApiAccess", Roles = "Admin,Manager")]
    public Task<IActionResult> RetrySap(Guid id, CancellationToken ct) =>
        Run(caller => service.RetrySapAsync(caller, id, ct));

    /// <summary>
    /// Records the SAP memo a person raised by hand for a ManualInSap credit. It ends the ledger's hold
    /// on the returned units, so managers only.
    /// </summary>
    [HttpPost("{id:guid}/mark-raised")]
    [Authorize(Policy = "ApiAccess", Roles = "Admin,Manager")]
    public Task<IActionResult> MarkRaised(Guid id, [FromBody] MarkDesktopCreditRaisedRequest request, CancellationToken ct) =>
        Run(caller => service.MarkRaisedInSapAsync(caller, id, request.SapDocNum, ct));

    private async Task<IActionResult> Run<T>(Func<Guid, Task<T>> action)
    {
        var id = UserClaimReader.GetUserId(User);
        if (!id.HasValue) return Unauthorized();
        try { return Ok(await action(id.Value)); }
        catch (UnauthorizedAccessException ex) { return Problem(statusCode: 403, detail: ex.Message); }
        catch (InvalidOperationException ex) { return Problem(statusCode: 409, detail: ex.Message); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Desktop credit-note list operation did not complete");
            return Problem(statusCode: 503, detail: "The credit notes could not be read. Try again in a moment.");
        }
    }
}
