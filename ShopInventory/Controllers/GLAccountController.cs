using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Features.GLAccounts.Queries.GetGLAccounts;
using ShopInventory.Features.GLAccounts.Queries.GetGLAccountsByType;
using ShopInventory.Features.GLAccounts.Queries.GetGLAccountByCode;
using ShopInventory.Features.GLAccounts.Queries.GetGLAccountLedger;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

// The account list feeds every page's account pickers and stays on "ApiAccess"; a ledger is every
// posting against an account, so it takes reports.view, which every role /gl-accounts admits holds.
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class GLAccountController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Get all G/L accounts from SAP
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetGLAccounts(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetGLAccountsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Filter by type
    /// </summary>
    [HttpGet("type/{accountType}")]
    public async Task<IActionResult> GetGLAccountsByType(string accountType, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetGLAccountsByTypeQuery(accountType), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get specific account
    /// </summary>
    [HttpGet("{accountCode}")]
    public async Task<IActionResult> GetGLAccountByCode(string accountCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetGLAccountByCodeQuery(accountCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// The journal postings against one account over a date range, with a running balance and a
    /// check of the total against what SAP's own chart of accounts reports.
    /// </summary>
    [HttpGet("{accountCode}/ledger")]
    [RequirePermission(Permission.ViewReports)]
    public async Task<IActionResult> GetGLAccountLedger(
        string accountCode,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetGLAccountLedgerQuery(accountCode, fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
