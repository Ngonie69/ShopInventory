using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.GLAccounts.Queries.GetGLAccounts;
using ShopInventory.Features.GLAccounts.Queries.GetGLAccountsByType;
using ShopInventory.Features.GLAccounts.Queries.GetGLAccountByCode;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class GLAccountController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetGLAccounts(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetGLAccountsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("type/{accountType}")]
    public async Task<IActionResult> GetGLAccountsByType(string accountType, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetGLAccountsByTypeQuery(accountType), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{accountCode}")]
    public async Task<IActionResult> GetGLAccountByCode(string accountCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetGLAccountByCodeQuery(accountCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
