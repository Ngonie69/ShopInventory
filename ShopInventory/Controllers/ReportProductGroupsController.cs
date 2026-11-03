using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.ReportProductGroups.Commands.DeleteReportProductGroup;
using ShopInventory.Features.ReportProductGroups.Commands.SaveReportProductGroup;
using ShopInventory.Features.ReportProductGroups.Queries.GetReportProductGroups;
using ShopInventory.Models.Entities;

namespace ShopInventory.Controllers;

// Anyone who may read reports may read the groups; changing them is set-up, so Admin only.
[Route("api/report-product-groups")]
[Authorize(Policy = "ApiAccess")]
public class ReportProductGroupsController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetGroups([FromQuery] bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetReportProductGroupsQuery(includeInactive), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateGroup([FromBody] SaveReportProductGroupRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(request.ToCommand(null, User.Identity?.Name), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateGroup(int id, [FromBody] SaveReportProductGroupRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(request.ToCommand(id, User.Identity?.Name), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteGroup(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteReportProductGroupCommand(id, User.Identity?.Name), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }
}

public sealed class SaveReportProductGroupRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<SaveReportProductGroupMemberRequest> Members { get; set; } = new();

    public SaveReportProductGroupCommand ToCommand(int? id, string? updatedBy) => new(
        id, Name, Description, SortOrder, IsActive,
        Members.Select(m => new SaveReportProductGroupMember(m.Kind, m.Code, m.Name)).ToList(),
        updatedBy);
}

public sealed class SaveReportProductGroupMemberRequest
{
    public ReportProductGroupMemberKind Kind { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
}
