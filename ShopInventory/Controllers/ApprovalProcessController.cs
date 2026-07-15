using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.Commands.CloseTransferRequest;
using ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

[Route("api/approval-process")]
[Authorize(Policy = "ApiAccess")]
public sealed class ApprovalProcessController(
    IInventoryTransferApprovalService approvalService,
    IMediator mediator) : ApiControllerBase
{
    [HttpGet("stages")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetStages(CancellationToken cancellationToken)
        => Ok(await approvalService.GetStagesAsync(cancellationToken));

    [HttpPost("stages")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SaveStage([FromBody] ApprovalStageDefinitionDto stage, CancellationToken cancellationToken)
    {
        var result = await approvalService.SaveStageAsync(stage, cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("stages/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteStage(Guid id, CancellationToken cancellationToken)
    {
        var result = await approvalService.DeleteStageAsync(id, cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    [HttpGet("templates")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetTemplates(CancellationToken cancellationToken)
        => Ok(await approvalService.GetTemplatesAsync(cancellationToken));

    [HttpPost("templates")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> SaveTemplate([FromBody] ApprovalTemplateDefinitionDto template, CancellationToken cancellationToken)
    {
        var result = await approvalService.SaveTemplateAsync(template, cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("templates/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteTemplate(Guid id, CancellationToken cancellationToken)
    {
        var result = await approvalService.DeleteTemplateAsync(id, cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    [HttpPost("transfer-requests/{docEntry:int}/decision")]
    public async Task<IActionResult> SubmitDecision(
        int docEntry,
        [FromBody] SubmitApprovalDecisionDto request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null) return Unauthorized();

        if (string.Equals(request.Decision, ApprovalDecisionValues.Approved, StringComparison.OrdinalIgnoreCase))
        {
            var result = await mediator.Send(
                new ConvertTransferRequestCommand(docEntry, userId.Value, request.StageId, request.Remarks, request.GenerateDocument), cancellationToken);
            return result.Match(value => Ok(value), errors => Problem(errors));
        }

        if (string.Equals(request.Decision, ApprovalDecisionValues.NotApproved, StringComparison.OrdinalIgnoreCase))
        {
            var result = await mediator.Send(
                new CloseTransferRequestCommand(docEntry, userId.Value, request.StageId, request.Remarks), cancellationToken);
            return result.Match(value => Ok(value), errors => Problem(errors));
        }

        return BadRequest(new { message = "Decision must be Approved or NotApproved." });
    }
}
